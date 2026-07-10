using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace UnityMCP.Editor.Tests
{
    public sealed class MCPCommandRegressionTests
    {
        private const string TEST_FOLDER = "Assets/__UnityMCPTests";
        private const string PREFAB_PATH = TEST_FOLDER + "/MCP Test Prefab.prefab";

        [SetUp]
        public void SetUp()
        {
            AssetDatabase.DeleteAsset(TEST_FOLDER);
            AssetDatabase.CreateFolder("Assets", "__UnityMCPTests");
        }

        [TearDown]
        public void TearDown()
        {
            var builderType = Type.GetType("Unity.UI.Builder.Builder, UnityEditor.UIBuilderModule", false);
            if (builderType != null)
            {
                foreach (var window in Resources.FindObjectsOfTypeAll(builderType).OfType<EditorWindow>())
                    window.Close();
            }

            AssetDatabase.DeleteAsset(TEST_FOLDER);
        }

        [Test]
        public void AddGameObject_InvalidParent_DoesNotModifyPrefab()
        {
            CreateTestPrefab();
            string absolutePath = GetAbsolutePath(PREFAB_PATH);
            byte[] before = File.ReadAllBytes(absolutePath);

            MCPPrefabAssetCommands.AddGameObject(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "parentPrefabPath", "Controls/Missing" },
                { "name", "Should Not Exist" },
            });

            CollectionAssert.AreEqual(before, File.ReadAllBytes(absolutePath));
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(root.transform.Find("Controls/Gold"), Is.Not.Null);
                Assert.That(root.transform.Find("Controls/Missing"), Is.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void AddGameObject_NestedRootPrefixedParent_ReturnsAccurateDiff()
        {
            CreateTestPrefab();
            var result = RequireDictionary(MCPPrefabAssetCommands.AddGameObject(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "parentPrefabPath", "MCP Test Prefab/Controls/Gold" },
                { "name", "Value Sync" },
                { "prefabFileDiffMode", "minimal" },
                { "prefabFileDiffMaxLines", 500 },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            var diff = RequireDictionary(result["prefabFileDiff"]);
            var summary = RequireDictionary(diff["summary"]);
            int added = System.Convert.ToInt32(summary["addedLineCount"]);
            int removed = System.Convert.ToInt32(summary["removedLineCount"]);
            Assert.That(added, Is.GreaterThan(0));
            Assert.That(removed, Is.LessThan(20));
            Assert.That(System.Convert.ToInt32(diff["changedLineCount"]), Is.EqualTo(added + removed));

            var lines = (List<Dictionary<string, object>>)diff["lines"];
            Assert.That(lines.Any(line => line["type"].ToString() == "added" &&
                                          line["text"].ToString().Contains("Value Sync")), Is.True);

            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(root.transform.Find("Controls/Gold/Value Sync"), Is.Not.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void MoveComponent_PreservesSerializedDataAndCleansYamlWhitespace()
        {
            CreateTestPrefab(addCollider: true);
            ReverseYamlObjectBlocks(PREFAB_PATH);
            var beforeBlockOrder = GetYamlObjectBlockKeys(PREFAB_PATH);
            var result = RequireDictionary(MCPPrefabAssetCommands.MoveComponent(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "sourcePrefabPath", "Source" },
                { "targetPrefabPath", "Target" },
                { "componentType", typeof(BoxCollider).FullName },
                { "prefabFileDiffMode", "summary" },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var source = root.transform.Find("Source").gameObject;
                var target = root.transform.Find("Target").gameObject;
                Assert.That(source.GetComponent<BoxCollider>(), Is.Null);

                var moved = target.GetComponent<BoxCollider>();
                Assert.That(moved, Is.Not.Null);
                Assert.That(moved.center, Is.EqualTo(new Vector3(1, 2, 3)));
                Assert.That(moved.size, Is.EqualTo(new Vector3(4, 5, 6)));
                Assert.That(moved.isTrigger, Is.True);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            string yaml = File.ReadAllText(GetAbsolutePath(PREFAB_PATH));
            Assert.That(Regex.IsMatch(yaml, @"[\t ]+(?=\r?$)", RegexOptions.Multiline), Is.False);
            var afterBlockOrder = GetYamlObjectBlockKeys(PREFAB_PATH);
            var survivingBefore = beforeBlockOrder.Where(afterBlockOrder.Contains).ToArray();
            var survivingAfter = afterBlockOrder.Where(beforeBlockOrder.Contains).ToArray();
            CollectionAssert.AreEqual(survivingBefore, survivingAfter);
            Assert.That(afterBlockOrder.Skip(survivingAfter.Length).All(key => !beforeBlockOrder.Contains(key)),
                Is.True);
        }

        [Test]
        public void SceneHierarchy_ComponentFilter_ReturnsCompactMatches()
        {
            string objectName = "__UnityMCP_Component_Filter_Target";
            var gameObject = new GameObject(objectName);
            gameObject.AddComponent<BoxCollider>();
            try
            {
                var result = RequireDictionary(MCPSceneCommands.GetHierarchy(new Dictionary<string, object>
                {
                    { "componentType", typeof(BoxCollider).FullName },
                    { "nameContains", objectName },
                    { "maxResults", 5 },
                }));

                Assert.That(result["filtered"], Is.EqualTo(true));
                Assert.That(System.Convert.ToInt32(result["matchCount"]), Is.EqualTo(1));
                var matches = (List<object>)result["matches"];
                var match = RequireDictionary(matches[0]);
                Assert.That(match["path"].ToString(), Does.EndWith(objectName));
                Assert.That(result.ContainsKey("hierarchy"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ExecuteCode_NestedCollectionsRemainStructured()
        {
            var response = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "return new { labels = new[] { \"a\", \"b\" }, values = new[] { 1, 2 }, nested = new { ok = true } };" },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            var result = RequireDictionary(response["result"]);
            var labels = (List<object>)result["labels"];
            var values = (List<object>)result["values"];
            var nested = RequireDictionary(result["nested"]);
            CollectionAssert.AreEqual(new[] { "a", "b" }, labels);
            CollectionAssert.AreEqual(new object[] { 1, 2 }, values);
            Assert.That(nested["ok"], Is.EqualTo(true));
        }

        [Test]
        public void ExecuteCode_ResultBudgetTruncatesLargeCollections()
        {
            var response = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "return Enumerable.Range(0, 10).ToArray();" },
                { "maxResultItems", 2 },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["truncated"], Is.EqualTo(true));
            Assert.That(System.Convert.ToInt32(response["count"]), Is.EqualTo(10));
            var result = (List<object>)response["result"];
            CollectionAssert.AreEqual(new object[] { 0, 1, "<truncated>" }, result);
        }

        [Test]
        public void ExecuteCode_UIElementsUsingAndUserLineNumbersAreAvailable()
        {
            var success = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "return new Button { name = \"Ready\" }.name;" },
            }));
            Assert.That(success["success"], Is.EqualTo(true));
            Assert.That(success["result"], Is.EqualTo("Ready"));

            var failure = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "var value = 1;\nreturn MissingSymbol;" },
            }));
            Assert.That(failure["error"], Is.EqualTo("Compilation failed"));
            var errors = (List<string>)failure["errors"];
            Assert.That(errors.Any(error => error.StartsWith("Line 2:", StringComparison.Ordinal)), Is.True);
        }

        [Test]
        public void ExecuteCode_UsesUnloadableAssemblyIsolation()
        {
            var response = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "return 7;" },
            }));
            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["assemblyIsolation"],
                Is.EqualTo("app-domain").Or.EqualTo("collectible-load-context"));
        }

        [Test]
        public void ExecuteCode_UnityReferencesAvoidIsolatedAppDomain()
        {
            var response = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "return UnityEditor.EditorApplication.isPlaying;" },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["assemblyIsolation"], Is.Not.EqualTo("app-domain"));
            Assert.That(response["assemblyIsolationReason"].ToString(), Does.Contain("UnityEditor"));
        }

        [Test]
        public void UIToolkitAssetInspect_NamesQueryIsCompactAndRelevant()
        {
            const string uxmlPath = TEST_FOLDER + "/Compact Inspect.uxml";
            const string ussPath = TEST_FOLDER + "/Compact Inspect.uss";
            File.WriteAllText(GetAbsolutePath(uxmlPath),
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"Target\" class=\"target-class\"/><ui:VisualElement name=\"Unrelated\" class=\"unrelated-class\"/></ui:UXML>");
            File.WriteAllText(GetAbsolutePath(ussPath),
                ".target-class { width: 10px; }\n.unrelated-class { width: 20px; }\n");
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);

            var result = RequireDictionary(MCPUICommands.InspectUIToolkitAsset(new Dictionary<string, object>
            {
                { "uxmlPath", uxmlPath },
                { "ussPath", ussPath },
                { "names", new[] { "Target" } },
                { "maxResults", 5 },
                { "includeUss", true },
            }));

            Assert.That(result["valid"], Is.EqualTo(true));
            Assert.That(result["includeElements"], Is.EqualTo(false));
            Assert.That(((List<Dictionary<string, object>>)result["elements"]), Is.Empty);
            var nameChecks = (List<Dictionary<string, object>>)result["nameChecks"];
            Assert.That(nameChecks, Has.Count.EqualTo(1));
            Assert.That(nameChecks[0]["reportedMatchCount"], Is.EqualTo(1));
            var ussClasses = (Dictionary<string, object>)result["ussClasses"];
            Assert.That(ussClasses.ContainsKey("target-class"), Is.True);
            Assert.That(ussClasses.ContainsKey("unrelated-class"), Is.False);
            Assert.That(result["outputTruncated"], Is.EqualTo(false));
        }

        [UnityTest]
        public IEnumerator UIBuilderPreview_WaitsForRequestedDocumentAndCanvas()
        {
            const string uxmlPath = TEST_FOLDER + "/Builder Preview.uxml";
            File.WriteAllText(GetAbsolutePath(uxmlPath),
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"PreviewTarget\" style=\"width: 120px; height: 80px;\"/></ui:UXML>");
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceSynchronousImport);

            object response = null;
            MCPUICommands.OpenUIBuilderPreview(new Dictionary<string, object>
            {
                { "uxmlPath", uxmlPath },
                { "waitFrames", 1 },
                { "stableFrames", 1 },
                { "timeoutMs", 10000 },
                { "capture", false },
            }, value => response = value);

            double timeoutAt = EditorApplication.timeSinceStartup + 12;
            while (response == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            Assert.That(response, Is.Not.Null);
            var result = RequireDictionary(response);
            Assert.That(result["success"], Is.EqualTo(true));
            var preview = RequireDictionary(result["preview"]);
            Assert.That(preview["ready"], Is.EqualTo(true));
            Assert.That(preview["documentPathMatches"], Is.EqualTo(true));
            Assert.That(preview["activeUxmlPath"], Is.EqualTo(uxmlPath));
        }

        [Test]
        public void GameViewScreenshot_RejectsEditModeImmediately()
        {
            const string screenshotPath = TEST_FOLDER + "/Game View.png";
            object response = null;
            MCPScreenshotCommands.CaptureGameView(new Dictionary<string, object>
            {
                { "path", screenshotPath },
                { "waitFrames", 1 },
                { "stableFrames", 1 },
                { "timeoutMs", 10000 },
            }, value => response = value);

            Assert.That(response, Is.Not.Null);
            var result = RequireDictionary(response);
            Assert.That(result["success"], Is.EqualTo(false));
            Assert.That(result["errorCode"], Is.EqualTo("requires_play_mode"));
            Assert.That(result["requiresPlayMode"], Is.EqualTo(true));
            Assert.That(File.Exists(GetAbsolutePath(screenshotPath)), Is.False);
        }

        [Test]
        public void ProjectToolNamesStayReadableAndBelowClientLimit()
        {
            var method = typeof(MCPToolMetadata).GetMethod("ProjectToolNameToToolName",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null);

            string runtimeName = method.Invoke(null,
                new object[] { "battleidle/get-runtime-ready-state", "" }).ToString();
            string validationName = method.Invoke(null,
                new object[] { "vmframework/validate-visual-element-paths", "" }).ToString();
            Assert.That(runtimeName, Is.EqualTo("unity_pt_battle_get_runtime_ready_state"));
            Assert.That(validationName, Is.EqualTo("unity_pt_vmf_validate_ui_el_paths"));
            Assert.That(runtimeName.Length, Is.LessThanOrEqualTo(48));
            Assert.That(validationName.Length, Is.LessThanOrEqualTo(48));
        }

        [Test]
        public void PrefabHierarchy_MaxNodesReturnsTruncationMetadata()
        {
            CreateTestPrefab();
            var result = RequireDictionary(MCPPrefabAssetCommands.GetHierarchy(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "maxNodes", 2 },
            }));

            Assert.That(System.Convert.ToInt32(result["returnedNodes"]), Is.EqualTo(2));
            Assert.That(System.Convert.ToInt32(result["totalNodes"]), Is.GreaterThan(2));
            Assert.That(result["truncated"], Is.EqualTo(true));
        }

        [Test]
        public void PrefabSetReference_ResolvesSpriteSubAssetInsteadOfTextureMainAsset()
        {
            const string spritePath = TEST_FOLDER + "/Reference Sprite.png";
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply();
                File.WriteAllBytes(GetAbsolutePath(spritePath), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(spritePath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();

            Assert.That(AssetDatabase.LoadAssetAtPath<Object>(spritePath), Is.TypeOf<Texture2D>());
            Assert.That(AssetDatabase.LoadAssetAtPath<Sprite>(spritePath), Is.Not.Null);

            var prefabRoot = new GameObject("Sprite Reference Prefab");
            try
            {
                prefabRoot.AddComponent<SpriteRenderer>();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(prefabRoot, PREFAB_PATH), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(prefabRoot);
            }

            var result = RequireDictionary(MCPPrefabAssetCommands.SetReference(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "componentType", typeof(SpriteRenderer).FullName },
                { "propertyName", "m_Sprite" },
                { "referenceAssetPath", spritePath },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(result["reference"].ToString(), Does.Contain("Sprite"));

            var loadedRoot = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var sprite = loadedRoot.GetComponent<SpriteRenderer>().sprite;
                Assert.That(sprite, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(sprite), Is.EqualTo(spritePath));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loadedRoot);
            }
        }

        [Test]
        public void RuntimeUIDocumentSelector_AcceptsNegativeInstanceId()
        {
            var firstObject = new GameObject("__UnityMCP_UIDocument_A");
            var targetObject = new GameObject("__UnityMCP_UIDocument_Z");
            try
            {
                firstObject.AddComponent<UIDocument>();
                var targetDocument = targetObject.AddComponent<UIDocument>();

                var buildInfo = typeof(MCPUICommands).GetMethod("BuildUIDocumentInfo",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var findDocument = typeof(MCPUICommands).GetMethod("FindRuntimeUIDocument",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(buildInfo, Is.Not.Null);
                Assert.That(findDocument, Is.Not.Null);

                var documentInfo = RequireDictionary(buildInfo.Invoke(null, new object[] { targetDocument }));
                string instanceId = documentInfo["instanceId"].ToString();
#if !UNITY_6000_5_OR_NEWER
                Assert.That(instanceId, Does.StartWith("-"));
                object requestInstanceId = Convert.ToInt64(instanceId);
#else
                object requestInstanceId = instanceId;
#endif

                object[] parameters =
                {
                    new Dictionary<string, object> { { "documentInstanceId", requestInstanceId } },
                    null,
                };
                var resolvedDocument = findDocument.Invoke(null, parameters);

                Assert.That(resolvedDocument, Is.SameAs(targetDocument));
                Assert.That(parameters[1], Is.EqualTo(""));
            }
            finally
            {
                Object.DestroyImmediate(targetObject);
                Object.DestroyImmediate(firstObject);
            }
        }

        [Test]
        public void ToolMetadata_DefaultIsCompactPaginatedAndSchemaFree()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools());
            Assert.That(System.Convert.ToInt32(result["schemaVersion"]), Is.EqualTo(3));
            Assert.That(result["compact"], Is.EqualTo(true));
            Assert.That(result["firstClassOnly"], Is.EqualTo(true));
            Assert.That(System.Convert.ToInt32(result["returnedTools"]), Is.LessThanOrEqualTo(50));
            Assert.That(result.ContainsKey("routes"), Is.False);
            Assert.That(result.ContainsKey("mcpTools"), Is.False);
            Assert.That(MiniJson.Serialize(result).Length, Is.LessThan(100000));

            var tools = (List<Dictionary<string, object>>)result["tools"];
            Assert.That(tools.All(tool => !tool.ContainsKey("inputSchema")), Is.True);
        }

        [Test]
        public void ToolMetadata_DetailedPageDoesNotDuplicateSchemaAliases()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: false, compact: false, includeSchema: true, limit: 5));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            Assert.That(tools, Is.Not.Empty);
            Assert.That(tools.All(tool => tool.ContainsKey("inputSchema")), Is.True);
            Assert.That(tools.All(tool => !tool.ContainsKey("input_schema")), Is.True);
            Assert.That(result.ContainsKey("firstClassTools"), Is.False);
            Assert.That(result.ContainsKey("fallbackTools"), Is.False);
        }

        [Test]
        public void ToolMetadata_LocalizationRoutesRequireOptionalPackage()
        {
            var method = typeof(MCPToolMetadata).GetMethod("IsRouteAvailable",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { "localization/status", false }), Is.EqualTo(false));
            Assert.That(method.Invoke(null, new object[] { "localization/status", true }), Is.EqualTo(true));
            Assert.That(method.Invoke(null, new object[] { "scene/hierarchy", false }), Is.EqualTo(true));
        }

        private static void CreateTestPrefab(bool addCollider = false)
        {
            var root = new GameObject("MCP Test Prefab");
            try
            {
                var controls = new GameObject("Controls");
                controls.transform.SetParent(root.transform, false);
                var gold = new GameObject("Gold");
                gold.transform.SetParent(controls.transform, false);
                var source = new GameObject("Source");
                source.transform.SetParent(root.transform, false);
                var target = new GameObject("Target");
                target.transform.SetParent(root.transform, false);

                if (addCollider)
                {
                    var collider = source.AddComponent<BoxCollider>();
                    collider.center = new Vector3(1, 2, 3);
                    collider.size = new Vector3(4, 5, 6);
                    collider.isTrigger = true;
                }

                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static Dictionary<string, object> RequireDictionary(object value)
        {
            Assert.That(value, Is.TypeOf<Dictionary<string, object>>());
            return (Dictionary<string, object>)value;
        }

        private static void ReverseYamlObjectBlocks(string assetPath)
        {
            string absolutePath = GetAbsolutePath(assetPath);
            string text = File.ReadAllText(absolutePath).Replace("\r\n", "\n").Replace('\r', '\n');
            var matches = Regex.Matches(text, @"(?m)^--- !u!\d+ &-?\d+(?: stripped)?[\t ]*$");
            Assert.That(matches.Count, Is.GreaterThan(1));

            string preamble = text.Substring(0, matches[0].Index);
            var blocks = new List<string>();
            for (int index = 0; index < matches.Count; index++)
            {
                int end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
                blocks.Add(text.Substring(matches[index].Index, end - matches[index].Index));
            }
            blocks.Reverse();
            File.WriteAllText(absolutePath, preamble + string.Concat(blocks));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static string[] GetYamlObjectBlockKeys(string assetPath)
        {
            string text = File.ReadAllText(GetAbsolutePath(assetPath));
            return Regex.Matches(text, @"(?m)^--- !u!(\d+) &(-?\d+)(?: stripped)?[\t ]*$")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value + ":" + match.Groups[2].Value)
                .ToArray();
        }

        private static string GetAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }
}
