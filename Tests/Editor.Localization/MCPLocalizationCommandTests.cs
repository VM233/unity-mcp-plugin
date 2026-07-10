using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.Localization;
using UnityEngine;

namespace UnityMCP.Editor.Localization.Tests
{
    public sealed class MCPLocalizationCommandTests
    {
        private const string TestFolder = "Assets/__UnityMCPLocalizationTests";
        private const string EnglishCode = "x-mcp-en";
        private const string ChineseCode = "x-mcp-zh";

        private readonly Dictionary<string, byte[]> m_AssetSnapshots = new();

        [SetUp]
        public void SetUp()
        {
            CleanupLocales();
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

            Assert.That(tools, Has.Count.EqualTo(11));
            Assert.That(tools.All(tool => tool["route"].ToString().StartsWith("localization/")), Is.True);
            Assert.That(tools.All(tool => tool.ContainsKey("inputSchema")), Is.True);
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
    }
}
