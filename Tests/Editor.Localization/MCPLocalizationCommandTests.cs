using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.SmartFormat.Extensions;
using UnityEngine.Localization.SmartFormat.PersistentVariables;

namespace UnityMCP.Editor.Localization.Tests
{
    public sealed class MCPLocalizationCommandTests
    {
        private const string TestFolder = "Assets/__UnityMCPLocalizationTests";
        private const string EnglishCode = "x-mcp-en";
        private const string ChineseCode = "x-mcp-zh";
        private const string VariableGroup = "mcp-test";

        private readonly Dictionary<string, byte[]> m_AssetSnapshots = new();

        [SetUp]
        public void SetUp()
        {
            CleanupLocales();
            CleanupVariableGroup();
            CleanupAddressables();
            AssetDatabase.DeleteAsset(TestFolder);
            AssetDatabase.SaveAssets();
            CaptureAddressableSnapshots();
            AssetDatabase.CreateFolder("Assets", "__UnityMCPLocalizationTests");
        }

        [TearDown]
        public void TearDown()
        {
            CleanupLocales();
            CleanupVariableGroup();
            AssetDatabase.DeleteAsset(TestFolder);
            CleanupAddressables();
            AssetDatabase.SaveAssets();
            RestoreAddressableSnapshots();
            AssetDatabase.Refresh();
        }

        [Test]
        public void LocalizationTools_AreExposedWhenPackageIsInstalled()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 100,
                category: "localization"));
            var tools = (List<Dictionary<string, object>>)result["tools"];

            Assert.That(tools, Has.Count.EqualTo(15));
            Assert.That(tools.All(tool => tool["route"].ToString().StartsWith("localization/")), Is.True);
            Assert.That(tools.All(tool => tool.ContainsKey("inputSchema")), Is.True);
            Assert.That(tools.Any(tool => tool["route"].ToString() == "localization/upsert-entries"), Is.True);
        }

        [Test]
        public void BatchStringEntryWorkflow_PrevalidatesThenUpsertsAllTranslations()
        {
            CreateLocale(EnglishCode, "English");
            CreateLocale(ChineseCode, "Chinese");

            var collection = Execute("localization/create-collection", new Dictionary<string, object>
            {
                { "name", "MCP Batch Strings" },
                { "type", "string" },
                { "assetDirectory", TestFolder + "/Batch Tables" },
                { "locales", new[] { EnglishCode, ChineseCode } },
            });
            Assert.That(collection["success"], Is.EqualTo(true));

            var invalid = Execute("localization/upsert-entries", new Dictionary<string, object>
            {
                { "collection", "MCP Batch Strings" },
                { "entries", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "key", "Character" },
                            { "translations", new Dictionary<string, object> { { EnglishCode, "Character" } } },
                        },
                        new Dictionary<string, object>
                        {
                            { "key", "Inventory" },
                            { "translations", new Dictionary<string, object> { { "x-mcp-missing", "Inventory" } } },
                        },
                    }
                },
            });
            Assert.That(invalid["success"], Is.EqualTo(false));

            var afterInvalid = Execute("localization/entries", new Dictionary<string, object>
            {
                { "collection", "MCP Batch Strings" },
            });
            Assert.That(Convert.ToInt32(afterInvalid["total"]), Is.EqualTo(0));

            var batch = Execute("localization/upsert-entries", new Dictionary<string, object>
            {
                { "collection", "MCP Batch Strings" },
                { "entries", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "key", "Character" },
                            { "translations", new Dictionary<string, object>
                                {
                                    { EnglishCode, "Character" },
                                    { ChineseCode, "角色" },
                                }
                            },
                        },
                        new Dictionary<string, object>
                        {
                            { "key", "InventoryCount" },
                            { "smart", true },
                            { "translations", new Dictionary<string, object>
                                {
                                    { EnglishCode, "Inventory: {count}" },
                                    { ChineseCode, "背包：{count}" },
                                }
                            },
                        },
                    }
                },
            });
            Assert.That(batch["success"], Is.EqualTo(true));
            Assert.That(Convert.ToInt32(batch["entryCount"]), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(batch["translationCount"]), Is.EqualTo(4));
            Assert.That(Convert.ToInt32(batch["createdKeyCount"]), Is.EqualTo(2));
            Assert.That(Convert.ToInt32(batch["createdTranslationCount"]), Is.EqualTo(4));
            Assert.That(batch["saved"], Is.EqualTo(true));

            var updated = Execute("localization/upsert-entries", new Dictionary<string, object>
            {
                { "collection", "MCP Batch Strings" },
                { "entries", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "key", "Character" },
                            { "translations", new Dictionary<string, object> { { EnglishCode, "Hero" } } },
                        },
                    }
                },
            });
            Assert.That(Convert.ToInt32(updated["createdKeyCount"]), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(updated["createdTranslationCount"]), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(updated["updatedTranslationCount"]), Is.EqualTo(1));

            var listed = Execute("localization/entries", new Dictionary<string, object>
            {
                { "collection", "MCP Batch Strings" },
            });
            Assert.That(Convert.ToInt32(listed["total"]), Is.EqualTo(2));
            var listedEntries = (List<Dictionary<string, object>>)listed["entries"];
            var inventory = listedEntries.Single(entry => entry["key"].ToString() == "InventoryCount");
            var values = (List<Dictionary<string, object>>)inventory["values"];
            Assert.That(values.Single(value => value["locale"].ToString() == EnglishCode)["value"],
                Is.EqualTo("Inventory: {count}"));
            Assert.That(values.All(value => value["smart"].Equals(true)), Is.True);
            var character = listedEntries.Single(entry => entry["key"].ToString() == "Character");
            var characterValues = (List<Dictionary<string, object>>)character["values"];
            Assert.That(characterValues.Single(value => value["locale"].ToString() == EnglishCode)["value"],
                Is.EqualTo("Hero"));
        }

        [Test]
        public void StringEntryWorkflow_CreatesListsValidatesAndRemovesEntries()
        {
            CreateLocale(EnglishCode, "English");
            CreateLocale(ChineseCode, "Chinese");

            var collection = Execute("localization/create-collection", new Dictionary<string, object>
            {
                { "name", "MCP Test Strings" },
                { "type", "string" },
                { "assetDirectory", TestFolder + "/Tables" },
                { "locales", new[] { EnglishCode, ChineseCode } },
            });
            Assert.That(collection["success"], Is.EqualTo(true));

            var english = Execute("localization/upsert-entry", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
                { "locale", EnglishCode },
                { "key", "Greeting" },
                { "value", "Hello {player}" },
                { "smart", true },
            });
            Assert.That(english["success"], Is.EqualTo(true));

            var missingTranslation = Execute("localization/validate", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
            });
            Assert.That(missingTranslation["valid"], Is.EqualTo(false));
            Assert.That(Convert.ToInt32(missingTranslation["issueCount"]), Is.EqualTo(1));

            var chinese = Execute("localization/upsert-entry", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
                { "locale", ChineseCode },
                { "key", "Greeting" },
                { "value", "你好，{player}" },
                { "smart", true },
            });
            Assert.That(chinese["success"], Is.EqualTo(true));

            var valid = Execute("localization/validate", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
            });
            Assert.That(valid["valid"], Is.EqualTo(true));

            var entries = Execute("localization/entries", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
                { "locale", EnglishCode },
            });
            Assert.That(Convert.ToInt32(entries["total"]), Is.EqualTo(1));
            var entry = RequireDictionary(((List<Dictionary<string, object>>)entries["entries"])[0]);
            var value = RequireDictionary(((List<Dictionary<string, object>>)entry["values"])[0]);
            Assert.That(value["value"], Is.EqualTo("Hello {player}"));
            Assert.That(value["smart"], Is.EqualTo(true));

            var removed = Execute("localization/remove-entry", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
                { "key", "Greeting" },
            });
            Assert.That(removed["removed"], Is.EqualTo(true));

            entries = Execute("localization/entries", new Dictionary<string, object>
            {
                { "collection", "MCP Test Strings" },
            });
            Assert.That(Convert.ToInt32(entries["total"]), Is.EqualTo(0));
        }

        [Test]
        public void AssetCollection_CanBeCreatedAndDiscovered()
        {
            CreateLocale(EnglishCode, "English");
            var created = Execute("localization/create-collection", new Dictionary<string, object>
            {
                { "name", "MCP Test Assets" },
                { "type", "asset" },
                { "assetDirectory", TestFolder + "/Asset Tables" },
                { "locales", new[] { EnglishCode } },
            });
            Assert.That(created["success"], Is.EqualTo(true));
            Assert.That(created["type"], Is.EqualTo("asset"));

            var listed = Execute("localization/collections", new Dictionary<string, object>
            {
                { "type", "asset" },
                { "nameContains", "MCP Test Assets" },
            });
            Assert.That(Convert.ToInt32(listed["count"]), Is.EqualTo(1));
        }

        [Test]
        public void PersistentVariableWorkflow_CreatesUpdatesListsAndRemovesVariables()
        {
            var created = Execute("localization/upsert-variable", new Dictionary<string, object>
            {
                { "group", VariableGroup },
                { "name", "score" },
                { "type", "int" },
                { "value", 7 },
                { "groupAssetPath", TestFolder + "/MCP Variables.asset" },
            });
            Assert.That(created["success"], Is.EqualTo(true));
            Assert.That(created["createdGroup"], Is.EqualTo(true));
            Assert.That(created["createdVariable"], Is.EqualTo(true));

            var updated = Execute("localization/upsert-variable", new Dictionary<string, object>
            {
                { "group", VariableGroup },
                { "name", "score" },
                { "type", "int" },
                { "value", 9 },
            });
            Assert.That(updated["createdGroup"], Is.EqualTo(false));
            Assert.That(updated["createdVariable"], Is.EqualTo(false));

            var listed = Execute("localization/variables", new Dictionary<string, object>
            {
                { "group", VariableGroup },
            });
            Assert.That(Convert.ToInt32(listed["groupCount"]), Is.EqualTo(1));
            var group = RequireDictionary(((List<Dictionary<string, object>>)listed["groups"])[0]);
            var variable = RequireDictionary(((List<Dictionary<string, object>>)group["variables"])[0]);
            Assert.That(variable["name"], Is.EqualTo("score"));
            Assert.That(Convert.ToInt32(variable["value"]), Is.EqualTo(9));

            var removed = Execute("localization/remove-variable", new Dictionary<string, object>
            {
                { "group", VariableGroup },
                { "name", "score" },
            });
            Assert.That(removed["removed"], Is.EqualTo(true));
        }

        private static void CreateLocale(string code, string name)
        {
            var result = Execute("localization/create-locale", new Dictionary<string, object>
            {
                { "code", code },
                { "name", name },
                { "assetPath", $"{TestFolder}/{code}.asset" },
            });
            Assert.That(result["success"], Is.EqualTo(true));
        }

        private static Dictionary<string, object> Execute(string route, Dictionary<string, object> args)
        {
            return RequireDictionary(MCPLocalizationCommands.Execute(route, args));
        }

        private static Dictionary<string, object> RequireDictionary(object value)
        {
            Assert.That(value, Is.TypeOf<Dictionary<string, object>>());
            return (Dictionary<string, object>)value;
        }

        private static void CleanupLocales()
        {
            foreach (string code in new[] { EnglishCode, ChineseCode })
            {
                var locale = LocalizationEditorSettings.GetLocale(code);
                if (locale != null)
                    LocalizationEditorSettings.RemoveLocale(locale);
            }
        }

        private static void CleanupAddressables()
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return;

            bool changed = false;
            foreach (var group in settings.groups
                         .Where(group => group != null &&
                                         group.Name.IndexOf("x-mcp-", StringComparison.OrdinalIgnoreCase) >= 0)
                         .ToList())
            {
                settings.RemoveGroup(group);
                changed = true;
            }

            foreach (string label in new[] { "Locale-x-mcp-en", "Locale-x-mcp-zh" })
            {
                if (!settings.GetLabels().Contains(label))
                    continue;
                settings.RemoveLabel(label);
                changed = true;
            }

            if (changed)
                EditorUtility.SetDirty(settings);
        }

        private void CaptureAddressableSnapshots()
        {
            m_AssetSnapshots.Clear();
            CaptureSnapshot(AssetDatabase.GetAssetPath(LocalizationEditorSettings.ActiveLocalizationSettings));

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return;

            CaptureSnapshot(AssetDatabase.GetAssetPath(settings));
            CaptureSnapshot(AssetDatabase.GetAssetPath(settings.FindGroup("Localization-Locales")));
        }

        private void CaptureSnapshot(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;
            string absolutePath = GetAbsolutePath(assetPath);
            if (File.Exists(absolutePath))
                m_AssetSnapshots[assetPath] = File.ReadAllBytes(absolutePath);
        }

        private void RestoreAddressableSnapshots()
        {
            foreach (var snapshot in m_AssetSnapshots)
            {
                File.WriteAllBytes(GetAbsolutePath(snapshot.Key), snapshot.Value);
                AssetDatabase.ImportAsset(snapshot.Key, ImportAssetOptions.ForceSynchronousImport |
                                                         ImportAssetOptions.ForceUpdate);
            }
            m_AssetSnapshots.Clear();
        }

        private static string GetAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static void CleanupVariableGroup()
        {
            var database = LocalizationSettings.StringDatabase;
            var source = database?.SmartFormatter.GetSourceExtension<PersistentVariablesSource>();
            if (source == null || !source.Remove(VariableGroup))
                return;

            var settings = LocalizationEditorSettings.ActiveLocalizationSettings;
            if (settings != null)
                EditorUtility.SetDirty(settings);
        }
    }
}
