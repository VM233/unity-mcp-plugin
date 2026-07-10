using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Pseudo;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using Object = UnityEngine.Object;

namespace UnityMCP.Editor.Localization
{
    public static class MCPLocalizationCommands
    {
        public static object Execute(string route, Dictionary<string, object> args)
        {
            try
            {
                switch (route)
                {
                    case "localization/status":
                        return GetStatus();
                    case "localization/locales":
                        return ListLocales(args);
                    case "localization/create-locale":
                        return CreateLocale(args);
                    case "localization/set-selected-locale":
                        return SetSelectedLocale(args);
                    case "localization/collections":
                        return ListCollections(args);
                    case "localization/create-collection":
                        return CreateCollection(args);
                    case "localization/entries":
                        return ListEntries(args);
                    case "localization/upsert-entry":
                        return UpsertEntry(args);
                    case "localization/remove-entry":
                        return RemoveEntry(args);
                    case "localization/validate":
                        return Validate(args);
                    case "localization/settings":
                        return UpdateSettings(args);
                    default:
                        return Error($"Unknown Localization route '{route}'");
                }
            }
            catch (Exception exception)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", exception.Message },
                    { "stackTrace", exception.StackTrace },
                };
            }
        }

        private static object GetStatus()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Locale).Assembly);
            var settings = LocalizationEditorSettings.ActiveLocalizationSettings;
            var locales = LocalizationEditorSettings.GetLocales();
            var stringCollections = LocalizationEditorSettings.GetStringTableCollections();
            var assetCollections = LocalizationEditorSettings.GetAssetTableCollections();
            var result = BuildSettingsInfo(settings);
            result["success"] = true;
            result["packageName"] = package?.name ?? "com.unity.localization";
            result["packageVersion"] = package?.version ?? "";
            result["localeCount"] = locales.Count;
            result["stringCollectionCount"] = stringCollections.Count;
            result["assetCollectionCount"] = assetCollections.Count;
            return result;
        }

        private static object ListLocales(Dictionary<string, object> args)
        {
            bool includePseudo = GetBool(args, "includePseudo", true);
            var locales = LocalizationEditorSettings.GetLocales()
                .Where(locale => includePseudo || !(locale is PseudoLocale))
                .OrderBy(locale => locale.Identifier.Code, StringComparer.OrdinalIgnoreCase)
                .Select(BuildLocaleInfo)
                .ToList();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "count", locales.Count },
                { "locales", locales },
            };
        }

        private static object CreateLocale(Dictionary<string, object> args)
        {
            string code = GetString(args, "code");
            string assetPath = NormalizeAssetPath(GetString(args, "assetPath"));
            if (string.IsNullOrWhiteSpace(code))
                return Error("code is required");
            if (!IsAssetPath(assetPath, ".asset"))
                return Error("assetPath must be under Assets and end in .asset");
            if (LocalizationEditorSettings.GetLocale(code) != null)
                return Error($"Locale '{code}' is already registered");
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) != null)
                return Error($"An asset already exists at '{assetPath}'");

            EnsureAssetDirectory(Path.GetDirectoryName(assetPath)?.Replace('\\', '/'));
            var locale = Locale.CreateLocale(new LocaleIdentifier(code));
            string displayName = GetString(args, "name");
            if (!string.IsNullOrWhiteSpace(displayName))
                locale.name = displayName;

            AssetDatabase.CreateAsset(locale, assetPath);
            if (GetBool(args, "addToProject", true))
                LocalizationEditorSettings.AddLocale(locale);
            AssetDatabase.SaveAssets();

            var result = BuildLocaleInfo(locale);
            result["success"] = true;
            result["registered"] = LocalizationEditorSettings.GetLocale(code) != null;
            return result;
        }

        private static object SetSelectedLocale(Dictionary<string, object> args)
        {
            string code = GetString(args, "locale");
            var locale = FindLocale(code, out string error);
            if (locale == null)
                return Error(error);

            var previous = LocalizationEditorSettings.ActiveLocalizationSettings?.GetSelectedLocale();
            LocalizationSettings.SelectedLocale = locale;
            return new Dictionary<string, object>
            {
                { "success", true },
                { "previousLocale", previous?.Identifier.Code ?? "" },
                { "selectedLocale", locale.Identifier.Code },
            };
        }

        private static object ListCollections(Dictionary<string, object> args)
        {
            string type = NormalizeCollectionType(GetString(args, "type"), allowEmpty: true);
            if (type == null)
                return Error("type must be string or asset");
            string nameContains = GetString(args, "nameContains");

            var collections = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(type) || type == "string")
                collections.AddRange(LocalizationEditorSettings.GetStringTableCollections()
                    .Select(collection => BuildCollectionInfo(collection, "string")));
            if (string.IsNullOrEmpty(type) || type == "asset")
                collections.AddRange(LocalizationEditorSettings.GetAssetTableCollections()
                    .Select(collection => BuildCollectionInfo(collection, "asset")));

            if (!string.IsNullOrEmpty(nameContains))
            {
                collections = collections.Where(collection => collection["name"].ToString()
                        .IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            collections = collections.OrderBy(collection => collection["name"].ToString(),
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "count", collections.Count },
                { "collections", collections },
            };
        }

        private static object CreateCollection(Dictionary<string, object> args)
        {
            string name = GetString(args, "name");
            string type = NormalizeCollectionType(GetString(args, "type"));
            string assetDirectory = NormalizeAssetPath(GetString(args, "assetDirectory")).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(name))
                return Error("name is required");
            if (type == null)
                return Error("type must be string or asset");
            if (!IsAssetPath(assetDirectory))
                return Error("assetDirectory must be under Assets");

            var existing = GetCollection(name, type);
            if (existing != null)
                return Error($"{type} Table Collection '{name}' already exists");

            var localeCodes = GetStringList(args, "locales");
            var locales = new List<Locale>();
            if (localeCodes.Count == 0)
            {
                locales.AddRange(LocalizationEditorSettings.GetLocales());
            }
            else
            {
                foreach (string localeCode in localeCodes)
                {
                    var locale = FindLocale(localeCode, out string error);
                    if (locale == null)
                        return Error(error);
                    if (!locales.Contains(locale))
                        locales.Add(locale);
                }
            }

            EnsureAssetDirectory(assetDirectory);
            LocalizationTableCollection collection = type == "asset"
                ? LocalizationEditorSettings.CreateAssetTableCollection(name, assetDirectory, locales)
                : LocalizationEditorSettings.CreateStringTableCollection(name, assetDirectory, locales);
            if (collection == null)
                return Error($"Failed to create {type} Table Collection '{name}'");

            string group = GetString(args, "group");
            if (!string.IsNullOrWhiteSpace(group))
                collection.Group = group;
            if (args.ContainsKey("preload"))
                collection.SetPreloadTableFlag(GetBool(args, "preload", false));
            EditorUtility.SetDirty(collection);
            AssetDatabase.SaveAssets();

            var result = BuildCollectionInfo(collection, type);
            result["success"] = true;
            return result;
        }

        private static object ListEntries(Dictionary<string, object> args)
        {
            string type = NormalizeCollectionType(GetString(args, "type"));
            if (type == null)
                return Error("type must be string or asset");
            var collection = GetCollection(GetString(args, "collection"), type);
            if (collection == null)
                return Error($"{type} Table Collection '{GetString(args, "collection")}' was not found");

            string localeFilter = GetString(args, "locale");
            string keyContains = GetString(args, "keyContains");
            int offset = Math.Max(0, GetInt(args, "offset", 0));
            int limit = Math.Max(1, Math.Min(GetInt(args, "limit", 100), 500));
            var sharedEntries = collection.SharedData.Entries
                .Where(entry => string.IsNullOrEmpty(keyContains) ||
                                entry.Key.IndexOf(keyContains, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entries = sharedEntries.Skip(offset).Take(limit)
                .Select(entry => BuildEntryInfo(collection, type, entry, localeFilter))
                .ToList();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "collection", collection.TableCollectionName },
                { "type", type },
                { "total", sharedEntries.Count },
                { "offset", offset },
                { "limit", limit },
                { "returned", entries.Count },
                { "hasMore", offset + entries.Count < sharedEntries.Count },
                { "entries", entries },
            };
        }

        private static object UpsertEntry(Dictionary<string, object> args)
        {
            string type = NormalizeCollectionType(GetString(args, "type"));
            if (type == null)
                return Error("type must be string or asset");
            string collectionName = GetString(args, "collection");
            string localeCode = GetString(args, "locale");
            string key = GetString(args, "key");
            if (string.IsNullOrWhiteSpace(key))
                return Error("key is required");

            var collection = GetCollection(collectionName, type);
            if (collection == null)
                return Error($"{type} Table Collection '{collectionName}' was not found");
            var locale = FindLocale(localeCode, out string localeError);
            if (locale == null)
                return Error(localeError);

            var table = collection.GetTable(locale.Identifier);
            if (table == null && GetBool(args, "createTable", true))
                table = collection.AddNewTable(locale.Identifier);
            if (table == null)
                return Error($"Collection '{collectionName}' has no table for Locale '{localeCode}'");

            bool created = collection.SharedData.GetEntry(key) == null;
            if (type == "string")
            {
                string value = GetString(args, "value");
                var stringTable = (StringTable)table;
                var entry = stringTable.GetEntry(key) ?? stringTable.AddEntry(key, value);
                entry.Value = value;
                if (args.ContainsKey("smart"))
                    entry.IsSmart = GetBool(args, "smart", false);
                EditorUtility.SetDirty(stringTable);
                EditorUtility.SetDirty(stringTable.SharedData);
            }
            else
            {
                string assetPath = NormalizeAssetPath(GetString(args, "assetPath"));
                if (!IsAssetPath(assetPath))
                    return Error("assetPath under Assets is required for an Asset Table entry");
                Object asset = LoadAsset(assetPath, GetString(args, "subAssetName"));
                if (asset == null)
                    return Error($"Asset was not found at '{assetPath}'");
                ((AssetTableCollection)collection).AddAssetToTable(locale.Identifier, key, asset);
            }

            AssetDatabase.SaveAssets();
            var sharedEntry = collection.SharedData.GetEntry(key);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "created", created },
                { "collection", collection.TableCollectionName },
                { "type", type },
                { "locale", locale.Identifier.Code },
                { "key", key },
                { "keyId", sharedEntry?.Id ?? 0 },
            };
        }

        private static object RemoveEntry(Dictionary<string, object> args)
        {
            string type = NormalizeCollectionType(GetString(args, "type"));
            if (type == null)
                return Error("type must be string or asset");
            string collectionName = GetString(args, "collection");
            string key = GetString(args, "key");
            if (string.IsNullOrWhiteSpace(key))
                return Error("key is required");

            var collection = GetCollection(collectionName, type);
            if (collection == null)
                return Error($"{type} Table Collection '{collectionName}' was not found");
            bool existed = collection.SharedData.GetEntry(key) != null;
            string localeCode = GetString(args, "locale");
            if (string.IsNullOrEmpty(localeCode))
            {
                if (existed)
                    collection.RemoveEntry(key);
            }
            else
            {
                var locale = FindLocale(localeCode, out string localeError);
                if (locale == null)
                    return Error(localeError);
                var table = collection.GetTable(locale.Identifier);
                if (table == null)
                    return Error($"Collection '{collectionName}' has no table for Locale '{localeCode}'");

                if (type == "asset")
                    ((AssetTableCollection)collection).RemoveAssetFromTable((AssetTable)table, key);
                else
                    ((StringTable)table).RemoveEntry(key);
                EditorUtility.SetDirty(table);
                EditorUtility.SetDirty(table.SharedData);
            }

            AssetDatabase.SaveAssets();
            return new Dictionary<string, object>
            {
                { "success", true },
                { "removed", existed },
                { "collection", collection.TableCollectionName },
                { "type", type },
                { "locale", localeCode },
                { "key", key },
            };
        }

        private static object Validate(Dictionary<string, object> args)
        {
            string typeFilter = NormalizeCollectionType(GetString(args, "type"), allowEmpty: true);
            if (typeFilter == null)
                return Error("type must be string or asset");
            string collectionFilter = GetString(args, "collection");
            bool includeEmpty = GetBool(args, "includeEmpty", true);
            int maxIssues = Math.Max(1, Math.Min(GetInt(args, "maxIssues", 200), 2000));

            var collections = new List<(LocalizationTableCollection collection, string type)>();
            if (string.IsNullOrEmpty(typeFilter) || typeFilter == "string")
                collections.AddRange(LocalizationEditorSettings.GetStringTableCollections()
                    .Select(collection => ((LocalizationTableCollection)collection, "string")));
            if (string.IsNullOrEmpty(typeFilter) || typeFilter == "asset")
                collections.AddRange(LocalizationEditorSettings.GetAssetTableCollections()
                    .Select(collection => ((LocalizationTableCollection)collection, "asset")));
            if (!string.IsNullOrEmpty(collectionFilter))
                collections = collections.Where(item => MatchesCollection(item.collection, collectionFilter)).ToList();

            var issues = new List<Dictionary<string, object>>();
            int totalIssues = 0;
            foreach (var item in collections)
            {
                foreach (var duplicate in item.collection.SharedData.Entries
                             .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                             .Where(group => group.Count() > 1))
                {
                    AddIssue(issues, maxIssues, ref totalIssues, item.collection, item.type,
                        duplicate.First(), "", "duplicate-key");
                }

                foreach (var sharedEntry in item.collection.SharedData.Entries)
                {
                    foreach (var table in GetTables(item.collection, item.type))
                    {
                        string issue = GetEntryIssue(table, item.type, sharedEntry.Id, includeEmpty);
                        if (!string.IsNullOrEmpty(issue))
                        {
                            AddIssue(issues, maxIssues, ref totalIssues, item.collection, item.type,
                                sharedEntry, table.LocaleIdentifier.Code, issue);
                        }
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "valid", totalIssues == 0 },
                { "collectionCount", collections.Count },
                { "issueCount", totalIssues },
                { "returnedIssueCount", issues.Count },
                { "truncated", issues.Count < totalIssues },
                { "issues", issues },
            };
        }

        private static object UpdateSettings(Dictionary<string, object> args)
        {
            var settings = LocalizationEditorSettings.ActiveLocalizationSettings;
            if (settings == null)
                return Error("No active Localization Settings asset was found");

            var changed = new List<string>();
            if (args.ContainsKey("initializeSynchronously"))
            {
                LocalizationSettings.InitializeSynchronously = GetBool(args, "initializeSynchronously", false);
                changed.Add("initializeSynchronously");
            }

            if (args.ContainsKey("projectLocale"))
            {
                var locale = FindLocale(GetString(args, "projectLocale"), out string error);
                if (locale == null)
                    return Error(error);
                LocalizationSettings.ProjectLocale = locale;
                changed.Add("projectLocale");
            }

            if (args.ContainsKey("selectedLocale"))
            {
                var locale = FindLocale(GetString(args, "selectedLocale"), out string error);
                if (locale == null)
                    return Error(error);
                LocalizationSettings.SelectedLocale = locale;
                changed.Add("selectedLocale");
            }

            if (changed.Count > 0)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }

            var result = BuildSettingsInfo(settings);
            result["success"] = true;
            result["changed"] = changed;
            return result;
        }

        private static Dictionary<string, object> BuildLocaleInfo(Locale locale)
        {
            return new Dictionary<string, object>
            {
                { "code", locale.Identifier.Code },
                { "name", locale.LocaleName },
                { "assetPath", AssetDatabase.GetAssetPath(locale) },
                { "isPseudo", locale is PseudoLocale },
            };
        }

        private static Dictionary<string, object> BuildCollectionInfo(LocalizationTableCollection collection,
            string type)
        {
            var tables = GetTables(collection, type)
                .Select(table => new Dictionary<string, object>
                {
                    { "locale", table.LocaleIdentifier.Code },
                    { "assetPath", AssetDatabase.GetAssetPath(table) },
                    { "entryCount", type == "asset" ? ((AssetTable)table).Count : ((StringTable)table).Count },
                    { "preload", LocalizationEditorSettings.GetPreloadTableFlag(table) },
                })
                .ToList();
            return new Dictionary<string, object>
            {
                { "name", collection.TableCollectionName },
                { "guid", collection.SharedData.TableCollectionNameGuid.ToString() },
                { "type", type },
                { "group", collection.Group },
                { "assetPath", AssetDatabase.GetAssetPath(collection) },
                { "sharedDataPath", AssetDatabase.GetAssetPath(collection.SharedData) },
                { "keyCount", collection.SharedData.Entries.Count },
                { "tableCount", tables.Count },
                { "tables", tables },
            };
        }

        private static Dictionary<string, object> BuildEntryInfo(LocalizationTableCollection collection,
            string type, SharedTableData.SharedTableEntry sharedEntry, string localeFilter)
        {
            var values = GetTables(collection, type)
                .Where(table => string.IsNullOrEmpty(localeFilter) ||
                                string.Equals(table.LocaleIdentifier.Code, localeFilter,
                                    StringComparison.OrdinalIgnoreCase))
                .Select(table => BuildLocalizedValue(table, type, sharedEntry.Id))
                .ToList();
            return new Dictionary<string, object>
            {
                { "key", sharedEntry.Key },
                { "keyId", sharedEntry.Id },
                { "values", values },
            };
        }

        private static Dictionary<string, object> BuildLocalizedValue(LocalizationTable table, string type,
            long keyId)
        {
            if (type == "asset")
            {
                var entry = ((AssetTable)table).GetEntry(keyId);
                return new Dictionary<string, object>
                {
                    { "locale", table.LocaleIdentifier.Code },
                    { "missing", entry == null },
                    { "empty", entry == null || entry.IsEmpty },
                    { "assetGuid", entry?.Guid ?? "" },
                    { "assetPath", entry != null ? AssetDatabase.GUIDToAssetPath(entry.Guid) : "" },
                    { "subAssetName", entry?.SubAssetName ?? "" },
                    { "address", entry?.Address ?? "" },
                };
            }

            var stringEntry = ((StringTable)table).GetEntry(keyId);
            return new Dictionary<string, object>
            {
                { "locale", table.LocaleIdentifier.Code },
                { "missing", stringEntry == null },
                { "empty", stringEntry == null || string.IsNullOrEmpty(stringEntry.Value) },
                { "value", stringEntry?.Value ?? "" },
                { "smart", stringEntry != null && stringEntry.IsSmart },
            };
        }

        private static Dictionary<string, object> BuildSettingsInfo(LocalizationSettings settings)
        {
            var selectedLocale = settings?.GetSelectedLocale();
            Locale projectLocale = settings != null ? LocalizationSettings.ProjectLocale : null;
            return new Dictionary<string, object>
            {
                { "hasActiveSettings", settings != null },
                { "settingsAssetPath", settings != null ? AssetDatabase.GetAssetPath(settings) : "" },
                { "selectedLocale", selectedLocale?.Identifier.Code ?? "" },
                { "projectLocale", projectLocale?.Identifier.Code ?? "" },
                { "initializeSynchronously", settings != null && LocalizationSettings.InitializeSynchronously },
                { "startupLocaleSelectors", settings != null
                    ? settings.GetStartupLocaleSelectors().Select(selector => selector.GetType().FullName).ToList()
                    : new List<string>() },
            };
        }

        private static List<LocalizationTable> GetTables(LocalizationTableCollection collection, string type)
        {
            return type == "asset"
                ? ((AssetTableCollection)collection).AssetTables.Cast<LocalizationTable>().ToList()
                : ((StringTableCollection)collection).StringTables.Cast<LocalizationTable>().ToList();
        }

        private static LocalizationTableCollection GetCollection(string nameOrGuid, string type)
        {
            if (string.IsNullOrWhiteSpace(nameOrGuid))
                return null;
            return type == "asset"
                ? LocalizationEditorSettings.GetAssetTableCollection(nameOrGuid)
                : LocalizationEditorSettings.GetStringTableCollection(nameOrGuid);
        }

        private static bool MatchesCollection(LocalizationTableCollection collection, string nameOrGuid)
        {
            return string.Equals(collection.TableCollectionName, nameOrGuid, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(collection.SharedData.TableCollectionNameGuid.ToString(), nameOrGuid,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static Locale FindLocale(string code, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(code))
            {
                error = "locale is required";
                return null;
            }

            var locale = LocalizationEditorSettings.GetLocale(code);
            if (locale == null)
                error = $"Registered Locale '{code}' was not found";
            return locale;
        }

        private static Object LoadAsset(string assetPath, string subAssetName)
        {
            if (string.IsNullOrEmpty(subAssetName))
                return AssetDatabase.LoadMainAssetAtPath(assetPath);
            return AssetDatabase.LoadAllAssetsAtPath(assetPath)
                .FirstOrDefault(asset => asset != null && asset.name == subAssetName);
        }

        private static string GetEntryIssue(LocalizationTable table, string type, long keyId,
            bool includeEmpty)
        {
            if (type == "asset")
            {
                var entry = ((AssetTable)table).GetEntry(keyId);
                if (entry == null)
                    return "missing";
                return includeEmpty && entry.IsEmpty ? "empty" : "";
            }

            var stringEntry = ((StringTable)table).GetEntry(keyId);
            if (stringEntry == null)
                return "missing";
            return includeEmpty && string.IsNullOrEmpty(stringEntry.Value) ? "empty" : "";
        }

        private static void AddIssue(List<Dictionary<string, object>> issues, int maxIssues,
            ref int totalIssues, LocalizationTableCollection collection, string type,
            SharedTableData.SharedTableEntry entry, string locale, string issue)
        {
            totalIssues++;
            if (issues.Count >= maxIssues)
                return;
            issues.Add(new Dictionary<string, object>
            {
                { "collection", collection.TableCollectionName },
                { "type", type },
                { "key", entry.Key },
                { "keyId", entry.Id },
                { "locale", locale },
                { "issue", issue },
            });
        }

        private static void EnsureAssetDirectory(string assetDirectory)
        {
            assetDirectory = NormalizeAssetPath(assetDirectory).TrimEnd('/');
            if (string.IsNullOrEmpty(assetDirectory) || assetDirectory == "Assets")
                return;
            if (!IsAssetPath(assetDirectory))
                throw new ArgumentException("Directory must be under Assets", nameof(assetDirectory));

            string current = "Assets";
            foreach (string part in assetDirectory.Substring("Assets".Length)
                         .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, part);
                current = next;
            }
        }

        private static string NormalizeCollectionType(string type, bool allowEmpty = false)
        {
            if (string.IsNullOrWhiteSpace(type))
                return allowEmpty ? "" : "string";
            if (type.Equals("string", StringComparison.OrdinalIgnoreCase))
                return "string";
            if (type.Equals("asset", StringComparison.OrdinalIgnoreCase))
                return "asset";
            return null;
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "").Replace('\\', '/').Trim();
        }

        private static bool IsAssetPath(string path, string requiredExtension = null)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                !(path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)))
                return false;
            return string.IsNullOrEmpty(requiredExtension) ||
                   path.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : "";
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;
            if (value is bool boolean)
                return boolean;
            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            return args != null && args.TryGetValue(key, out object value) && value != null &&
                   int.TryParse(value.ToString(), out int parsed)
                ? parsed
                : defaultValue;
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var result = new List<string>();
            if (args == null || !args.TryGetValue(key, out object value) || value == null || value is string)
                return result;
            if (!(value is IEnumerable values))
                return result;
            foreach (object item in values)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.ToString()))
                    result.Add(item.ToString());
            }
            return result;
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", message },
            };
        }
    }
}
