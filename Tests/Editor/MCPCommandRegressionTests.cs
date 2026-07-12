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
        public void MoveComponent_PreservesSerializedDataBlockOrderAndUntouchedWhitespace()
        {
            CreateTestPrefab(addCollider: true);
            ReverseYamlObjectBlocks(PREFAB_PATH);
            RewriteYamlObjectBlock(PREFAB_PATH, "m_Name: Gold",
                block => block.Replace("  m_TagString: Untagged\n", "  m_TagString: Untagged \n"));
            string untouchedBlockBefore = GetYamlObjectBlockContaining(PREFAB_PATH, "m_Name: Gold");
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
            string untouchedBlockAfter = GetYamlObjectBlockContaining(PREFAB_PATH, "m_Name: Gold");
            Assert.That(untouchedBlockAfter, Is.EqualTo(untouchedBlockBefore));
            var afterBlockOrder = GetYamlObjectBlockKeys(PREFAB_PATH);
            var survivingBefore = beforeBlockOrder.Where(afterBlockOrder.Contains).ToArray();
            var survivingAfter = afterBlockOrder.Where(beforeBlockOrder.Contains).ToArray();
            CollectionAssert.AreEqual(survivingBefore, survivingAfter);
            Assert.That(afterBlockOrder.Skip(survivingAfter.Length).All(key => !beforeBlockOrder.Contains(key)),
                Is.True);
        }

        [Test]
        public void MoveComponent_RemapsNestedSerializedReferenceToMovedComponent()
        {
            CreateTestPrefab(addCollider: true, addParticleReference: true);
            var result = RequireDictionary(MCPPrefabAssetCommands.MoveComponent(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "sourcePrefabPath", "Source" },
                { "targetPrefabPath", "Target" },
                { "componentType", typeof(BoxCollider).FullName },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(System.Convert.ToInt32(result["remappedReferenceCount"]), Is.EqualTo(1));

            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var moved = root.transform.Find("Target").GetComponent<BoxCollider>();
                var particleSystem = root.transform.Find("Reference Owner").GetComponent<ParticleSystem>();
                var trigger = particleSystem.trigger;

                Assert.That(moved, Is.Not.Null);
                Assert.That(trigger.GetCollider(0), Is.SameAs(moved));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void TransactionEdit_DoesNotSerializeDefaultsOnUntouchedComponents()
        {
            CreateTestPrefab(addCollider: true);
            RewriteYamlObjectBlock(PREFAB_PATH, "BoxCollider:",
                block => Regex.Replace(block, @"(?m)^  m_IsTrigger:.*\n", ""));
            string untouchedBlockBefore = GetYamlObjectBlockContaining(PREFAB_PATH, "BoxCollider:");
            Assert.That(untouchedBlockBefore, Does.Not.Contain("m_IsTrigger:"));

            var result = RequireDictionary(MCPPrefabAssetCommands.TransactionEdit(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "addGameObject" },
                            { "parentPrefabPath", "Controls" },
                            { "name", "Value Sync" },
                        },
                    }
                },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(GetYamlObjectBlockContaining(PREFAB_PATH, "BoxCollider:"),
                Is.EqualTo(untouchedBlockBefore));
        }

        [Test]
        public void TransactionEdit_SetProperty_ChangesSerializedArraySize()
        {
            CreateTestPrefab(addRenderer: true);

            var result = RequireDictionary(MCPPrefabAssetCommands.TransactionEdit(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "setProperty" },
                            { "prefabPath", "Source" },
                            { "componentType", typeof(MeshRenderer).FullName },
                            { "propertyName", "m_Materials.Array.size" },
                            { "value", 2 },
                        },
                    }
                },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(root.transform.Find("Source").GetComponent<MeshRenderer>().sharedMaterials.Length,
                    Is.EqualTo(2));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void TransactionEdit_ArrayOperations_EditListAtomically()
        {
            CreateTestPrefab(addRenderer: true);

            var result = RequireDictionary(MCPPrefabAssetCommands.TransactionEdit(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "arrayInsert" }, { "prefabPath", "Source" },
                            { "componentType", typeof(MeshRenderer).FullName },
                            { "propertyName", "m_Materials" }, { "index", 1 },
                        },
                        new Dictionary<string, object>
                        {
                            { "type", "arraySet" }, { "prefabPath", "Source" },
                            { "componentType", typeof(MeshRenderer).FullName },
                            { "propertyName", "m_Materials" }, { "index", 1 }, { "value", null },
                        },
                        new Dictionary<string, object>
                        {
                            { "type", "arrayRemove" }, { "prefabPath", "Source" },
                            { "componentType", typeof(MeshRenderer).FullName },
                            { "propertyName", "m_Materials" }, { "index", 0 },
                        },
                    }
                },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(root.transform.Find("Source").GetComponent<MeshRenderer>().sharedMaterials.Length,
                    Is.EqualTo(1));
                Assert.That(root.transform.Find("Source").GetComponent<MeshRenderer>().sharedMaterials[0], Is.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [UnityTest]
        public IEnumerator TransactionEditDeferred_LoadedComponentTypes_DoNotRefreshAssets()
        {
            CreateTestPrefab();
            const string sentinelPath = TEST_FOLDER + "/Refresh Sentinel.txt";
            File.WriteAllText(GetAbsolutePath(sentinelPath), "not imported");
            Assert.That(AssetDatabase.AssetPathToGUID(sentinelPath), Is.Empty);

            object completedResult = null;
            MCPPrefabAssetCommands.TransactionEditDeferred(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "refreshAssets", true },
                { "typeResolveStableMs", 0 },
                { "execution", new Dictionary<string, object>
                    {
                        { "mode", "batched" },
                        { "operationsPerFrame", 1 },
                        { "frameBudgetMs", 1 },
                    }
                },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "setProperty" },
                            { "prefabPath", "Source" },
                            { "componentType", typeof(Transform).FullName },
                            { "propertyName", "m_LocalPosition.x" },
                            { "value", 3f },
                        },
                    }
                },
            }, result => completedResult = result, _ => { });

            Assert.That(AssetDatabase.AssetPathToGUID(sentinelPath), Is.Empty,
                "The transaction refreshed the AssetDatabase even though every referenced type was already loaded.");

            double timeoutAt = EditorApplication.timeSinceStartup + 5d;
            while (completedResult == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            Assert.That(completedResult, Is.Not.Null);
            Assert.That(RequireDictionary(completedResult)["success"], Is.EqualTo(true));
        }

        [Test]
        public void UnifiedExecutionRoutes_AreDeferredAndLegacyBatchRoutesAreRemoved()
        {
            var routesProperty = typeof(MCPBridgeServer).GetProperty("DeferredRouteNames",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(routesProperty, Is.Not.Null);
            var routes = (IEnumerable<string>)routesProperty.GetValue(null);
            Assert.That(routes, Does.Contain("prefab-asset/transaction-edit"));
            Assert.That(routes, Does.Contain("asset/move"));
            Assert.That(routes, Does.Contain("component/set-reference"));
            Assert.That(routes, Does.Contain("localization/upsert-entry"));

            var registered = RequireDictionary(MCPToolMetadata.GetRegisteredRoutes());
            var registeredRoutes = (List<string>)registered["routes"];
            Assert.That(registeredRoutes, Does.Not.Contain("prefab-asset/batch-edit"));
            Assert.That(registeredRoutes, Does.Not.Contain("asset/move-batch"));
            Assert.That(registeredRoutes, Does.Not.Contain("component/batch-wire"));
            Assert.That(registeredRoutes, Does.Not.Contain("localization/upsert-entries"));
        }

        [Test]
        public void EditorUpdate_RequestProcessingIsBoundedAfterReload()
        {
            var maxRequestsField = typeof(MCPBridgeServer).GetField("MaxRequestsPerEditorUpdate",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(maxRequestsField, Is.Not.Null);
            Assert.That(maxRequestsField.GetRawConstantValue(), Is.EqualTo(1));

            var reloadDelayField = typeof(MCPBridgeServer).GetField("PostReloadProcessingDelaySeconds",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(reloadDelayField, Is.Not.Null);
            Assert.That((double)reloadDelayField.GetRawConstantValue(), Is.GreaterThanOrEqualTo(0.25));

            var processMethod = typeof(MCPRequestQueue).GetMethod("ProcessNextRequests",
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(int) }, null);
            Assert.That(processMethod, Is.Not.Null);
            Assert.That(processMethod.ReturnType, Is.EqualTo(typeof(int)));

            Assert.That(typeof(MCPBridgeServer).GetField("_mainThreadQueue",
                BindingFlags.Static | BindingFlags.NonPublic), Is.Null,
                "A second unbounded main-thread queue must not be reintroduced beside MCPRequestQueue.");
        }

        [Test]
        public void ExecutionOptions_ParseAndResolveMode()
        {
            Assert.That(MCPExecutionOptions.TryParse(new Dictionary<string, object>(), out var automatic,
                out string automaticError), Is.True, automaticError);
            Assert.That(automatic.ResolveMode(1), Is.EqualTo(MCPExecutionMode.Immediate));
            Assert.That(automatic.ResolveMode(2), Is.EqualTo(MCPExecutionMode.Batched));

            Assert.That(MCPExecutionOptions.TryParse(new Dictionary<string, object>
            {
                { "execution", new Dictionary<string, object>
                    {
                        { "mode", "batched" },
                        { "operationsPerFrame", 3 },
                        { "frameBudgetMs", 4 },
                        { "timeoutMs", 5000 },
                        { "continueOnError", true },
                    }
                },
            }, out var options, out string error), Is.True, error);
            Assert.That(options.ResolveMode(1), Is.EqualTo(MCPExecutionMode.Batched));
            Assert.That(options.OperationsPerFrame, Is.EqualTo(3));
            Assert.That(options.FrameBudgetMs, Is.EqualTo(4));
            Assert.That(options.TimeoutMs, Is.EqualTo(5000));
            Assert.That(options.ContinueOnError, Is.True);

            Assert.That(MCPExecutionOptions.TryParse(new Dictionary<string, object>
            {
                { "execution", new Dictionary<string, object> { { "mode", "parallel" } } },
            }, out _, out error), Is.False);
            Assert.That(error, Does.Contain("auto, immediate, or batched"));
        }

        [UnityTest]
        public IEnumerator AssetMoveDeferred_BatchedModeMovesAllAssets()
        {
            string first = TEST_FOLDER + "/First.txt";
            string second = TEST_FOLDER + "/Second.txt";
            string movedFirst = TEST_FOLDER + "/Moved First.txt";
            string movedSecond = TEST_FOLDER + "/Moved Second.txt";
            File.WriteAllText(GetAbsolutePath(first), "first");
            File.WriteAllText(GetAbsolutePath(second), "second");
            AssetDatabase.ImportAsset(first, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(second, ImportAssetOptions.ForceUpdate);

            object completed = null;
            MCPAssetCommands.MoveDeferred(new Dictionary<string, object>
            {
                { "moves", new object[]
                    {
                        new Dictionary<string, object> { { "path", first }, { "destinationPath", movedFirst } },
                        new Dictionary<string, object> { { "path", second }, { "destinationPath", movedSecond } },
                    }
                },
                { "execution", new Dictionary<string, object>
                    {
                        { "mode", "batched" },
                        { "operationsPerFrame", 1 },
                        { "frameBudgetMs", 1 },
                    }
                },
            }, result => completed = result, _ => { });

            double timeoutAt = EditorApplication.timeSinceStartup + 10d;
            while (completed == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            Assert.That(completed, Is.Not.Null);
            Assert.That(RequireDictionary(completed)["success"], Is.EqualTo(true));
            Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(movedFirst), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(movedSecond), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator ComponentSetReferencesDeferred_BatchedModeProcessesEveryReference()
        {
            var first = new GameObject("__MCP Reference First");
            var second = new GameObject("__MCP Reference Second");
            var firstCamera = first.AddComponent<Camera>();
            var secondCamera = second.AddComponent<Camera>();
            var renderTexture = new RenderTexture(8, 8, 0);
            string renderTexturePath = TEST_FOLDER + "/Reference Render Texture.asset";
            AssetDatabase.CreateAsset(renderTexture, renderTexturePath);

            object completed = null;
            MCPComponentCommands.SetReferencesDeferred(new Dictionary<string, object>
            {
                { "references", new object[]
                    {
                        new Dictionary<string, object>
                        {
                            { "path", first.name }, { "componentType", typeof(Camera).FullName },
                            { "propertyName", "m_TargetTexture" }, { "assetPath", renderTexturePath },
                        },
                        new Dictionary<string, object>
                        {
                            { "path", second.name }, { "componentType", typeof(Camera).FullName },
                            { "propertyName", "m_TargetTexture" }, { "assetPath", renderTexturePath },
                        },
                    }
                },
                { "execution", new Dictionary<string, object>
                    {
                        { "mode", "batched" }, { "operationsPerFrame", 1 }, { "frameBudgetMs", 1 },
                    }
                },
            }, result => completed = result, _ => { });

            double timeoutAt = EditorApplication.timeSinceStartup + 10d;
            while (completed == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            bool firstAssigned = firstCamera.targetTexture == renderTexture;
            bool secondAssigned = secondCamera.targetTexture == renderTexture;
            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
            Assert.That(completed, Is.Not.Null);
            Assert.That(RequireDictionary(completed)["success"], Is.EqualTo(true));
            Assert.That(firstAssigned, Is.True);
            Assert.That(secondAssigned, Is.True);
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
        public void ExecuteCode_IsNotPatternUsesLatestSupportedLanguageVersion()
        {
            var response = RequireDictionary(MCPEditorCommands.ExecuteCode(new Dictionary<string, object>
            {
                { "code", "object value = \"ready\"; return value is not int;" },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["result"], Is.EqualTo(true));
        }

        [Test]
        public void AssetRefresh_TargetedImportReconcilesExternalDeletion()
        {
            const string deletedPath = TEST_FOLDER + "/Externally Deleted.txt";
            const string importedPath = TEST_FOLDER + "/Imported First.txt";
            File.WriteAllText(GetAbsolutePath(deletedPath), "delete me");
            File.WriteAllText(GetAbsolutePath(importedPath), "keep me");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.AssetPathToGUID(deletedPath), Is.Not.Empty);

            File.Delete(GetAbsolutePath(deletedPath));
            File.Delete(GetAbsolutePath(deletedPath) + ".meta");

            var response = RequireDictionary(MCPAssetCommands.Refresh(new Dictionary<string, object>
            {
                { "assetPaths", new[] { importedPath } },
                { "forceUpdate", true },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["preReconciledExternalChanges"], Is.EqualTo(true));
            Assert.That(response["reconciledExternalChanges"], Is.EqualTo(true));
            Assert.That(File.Exists(GetAbsolutePath(deletedPath)), Is.False);
            Assert.That(File.Exists(GetAbsolutePath(deletedPath) + ".meta"), Is.False);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(deletedPath), Is.Null);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(deletedPath), Is.Null);
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
        public void TransformResponses_OmitIdentityValuesAndKeepNonDefaults()
        {
            var go = new GameObject("MCP Identity Transform");
            try
            {
                var identity = RequireDictionary(MCPGameObjectCommands.GetInfo(new Dictionary<string, object>
                {
                    { "path", go.name },
                }));
                foreach (string key in new[]
                         {
                             "position", "localPosition", "rotation", "localRotation", "scale", "lossyScale"
                         })
                {
                    Assert.That(identity.ContainsKey(key), Is.False, $"Identity response contained {key}");
                }

                go.transform.localPosition = new Vector3(1f, 2f, 3f);
                go.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
                go.transform.localScale = new Vector3(2f, 1f, 1f);

                var changed = RequireDictionary(MCPGameObjectCommands.GetInfo(new Dictionary<string, object>
                {
                    { "path", go.name },
                }));
                foreach (string key in new[]
                         {
                             "position", "localPosition", "rotation", "localRotation", "scale", "lossyScale"
                         })
                {
                    Assert.That(changed.ContainsKey(key), Is.True, $"Non-default response omitted {key}");
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void PrefabHierarchy_OmitsIdentityLocalTransformValues()
        {
            CreateTestPrefab();
            var result = RequireDictionary(MCPPrefabAssetCommands.GetHierarchy(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "maxNodes", 10 },
            }));
            var root = RequireDictionary(result["hierarchy"]);
            Assert.That(root.ContainsKey("localPosition"), Is.False);
            Assert.That(root.ContainsKey("localRotation"), Is.False);
            Assert.That(root.ContainsKey("localScale"), Is.False);
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
        public void PrefabSetReference_EmptyPrefabPathResolvesRootComponent()
        {
            var prefabRoot = new GameObject("Root Reference Prefab");
            try
            {
                prefabRoot.AddComponent<Rigidbody>();
                var owner = new GameObject("Owner");
                owner.transform.SetParent(prefabRoot.transform, false);
                owner.AddComponent<HingeJoint>();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(prefabRoot, PREFAB_PATH), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(prefabRoot);
            }

            var result = RequireDictionary(MCPPrefabAssetCommands.SetReference(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "prefabPath", "Owner" },
                { "componentType", typeof(HingeJoint).FullName },
                { "propertyName", "m_ConnectedBody" },
                { "referencePrefabPath", "" },
                { "referenceComponentType", typeof(Rigidbody).FullName },
            }));

            Assert.That(result["success"], Is.EqualTo(true));

            var clearResult = RequireDictionary(MCPPrefabAssetCommands.SetReference(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "prefabPath", "Owner" },
                { "componentType", typeof(HingeJoint).FullName },
                { "propertyName", "m_ConnectedBody" },
                { "clear", true },
            }));
            Assert.That(clearResult["success"], Is.EqualTo(true));

            var transactionResult = RequireDictionary(MCPPrefabAssetCommands.TransactionEdit(
                new Dictionary<string, object>
                {
                    { "assetPath", PREFAB_PATH },
                    { "operations", new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "setReference" },
                                { "prefabPath", "Owner" },
                                { "componentType", typeof(HingeJoint).FullName },
                                { "propertyName", "m_ConnectedBody" },
                                { "referencePrefabPath", "" },
                                { "referenceComponentType", typeof(Rigidbody).FullName },
                            },
                        }
                    },
                }));
            Assert.That(transactionResult["success"], Is.EqualTo(true));

            var loadedRoot = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(loadedRoot.transform.Find("Owner").GetComponent<HingeJoint>().connectedBody,
                    Is.SameAs(loadedRoot.GetComponent<Rigidbody>()));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loadedRoot);
            }
        }

        [Test]
        public void AssetImport_ConfiguresTextureImporterInOneOperation()
        {
            string externalPath = Path.Combine(Path.GetTempPath(), $"unity-mcp-{Guid.NewGuid():N}.png");
            const string spritePath = TEST_FOLDER + "/Imported Sprite.png";
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply();
                File.WriteAllBytes(externalPath, texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            try
            {
                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "sourcePath", externalPath }, { "destinationPath", spritePath },
                    { "textureType", "Sprite" }, { "spriteMode", "Single" },
                    { "pixelsPerUnit", 8f }, { "filterMode", "Point" }, { "isReadable", true },
                    { "compression", "uncompressed" }, { "alphaIsTransparency", true },
                    { "meshType", "FullRect" }, { "mipmapEnabled", false },
                }));

                Assert.That(result["success"], Is.EqualTo(true));
                var importer = (TextureImporter)AssetImporter.GetAtPath(spritePath);
                Assert.That(importer.textureType, Is.EqualTo(TextureImporterType.Sprite));
                Assert.That(importer.spriteImportMode, Is.EqualTo(SpriteImportMode.Single));
                Assert.That(importer.spritePixelsPerUnit, Is.EqualTo(8f));
                Assert.That(importer.filterMode, Is.EqualTo(FilterMode.Point));
                Assert.That(importer.isReadable, Is.True);
                Assert.That(importer.textureCompression, Is.EqualTo(TextureImporterCompression.Uncompressed));
                Assert.That(importer.alphaIsTransparency, Is.True);
                var serializedImporter = new SerializedObject(importer);
                Assert.That(serializedImporter.FindProperty("m_SpriteMeshType").intValue,
                    Is.EqualTo((int)SpriteMeshType.FullRect));
            }
            finally
            {
                if (File.Exists(externalPath)) File.Delete(externalPath);
            }
        }

        [Test]
        public void AssetRename_SynchronizesSingleSpriteSubAssetName()
        {
            const string oldPath = TEST_FOLDER + "/Old Sprite.png";
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply();
                File.WriteAllBytes(GetAbsolutePath(oldPath), texture.EncodeToPNG());
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(oldPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(oldPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();

            var result = RequireDictionary(MCPAssetCommands.Rename(new Dictionary<string, object>
            {
                { "path", oldPath }, { "newName", "Dragon King Head.png" },
            }));

            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(result["synchronizedSingleSpriteName"], Is.EqualTo(true));
            const string newPath = TEST_FOLDER + "/Dragon King Head.png";
            Assert.That(AssetDatabase.LoadAllAssetsAtPath(newPath).OfType<Sprite>().Single().name,
                Is.EqualTo("Dragon King Head"));
        }

        [Test]
        public void CleanupMissingVariantOverrides_RemovesOnlyInvalidPaths()
        {
            const string basePath = TEST_FOLDER + "/Base.prefab";
            const string variantPath = TEST_FOLDER + "/Variant.prefab";
            var root = new GameObject("Base");
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, basePath), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset);
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            PrefabUtility.SetPropertyModifications(variant, new[]
            {
                new PropertyModification
                {
                    target = baseAsset.transform, propertyPath = "m_LocalPosition.x", value = "2"
                },
                new PropertyModification
                {
                    target = baseAsset.transform, propertyPath = "m_RemovedSerializedField", value = "legacy"
                },
            });
            AssetDatabase.SaveAssets();

            var result = RequireDictionary(MCPPrefabAssetCommands.CleanupMissingVariantOverrides(
                new Dictionary<string, object> { { "assetPath", variantPath } }));
            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(Convert.ToInt32(result["removedCount"]), Is.EqualTo(1));
            var remaining = PrefabUtility.GetPropertyModifications(variant);
            Assert.That(remaining.Any(modification => modification.propertyPath == "m_LocalPosition.x"), Is.True);
            Assert.That(remaining.Any(modification => modification.propertyPath == "m_RemovedSerializedField"), Is.False);
        }

        [Test]
        public void CleanupMissingVariantOverrides_KeepsElementsAddedByArraySizeOverride()
        {
            const string basePath = TEST_FOLDER + "/Array Base.prefab";
            const string variantPath = TEST_FOLDER + "/Array Variant.prefab";
            var root = new GameObject("Array Base", typeof(MeshRenderer));
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, basePath), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }

            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(baseAsset);
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }

            var baseRenderer = baseAsset.GetComponent<MeshRenderer>();
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(variantPath);
            PrefabUtility.SetPropertyModifications(variant, new[]
            {
                new PropertyModification
                {
                    target = baseRenderer, propertyPath = "m_Materials.Array.size", value = "1"
                },
                new PropertyModification
                {
                    target = baseRenderer, propertyPath = "m_Materials.Array.data[0]", value = ""
                },
            });
            AssetDatabase.SaveAssets();
            int modificationCount = PrefabUtility.GetPropertyModifications(variant).Length;

            var result = RequireDictionary(MCPPrefabAssetCommands.CleanupMissingVariantOverrides(
                new Dictionary<string, object> { { "assetPath", variantPath }, { "dryRun", true } }));

            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(Convert.ToInt32(result["removedCount"]), Is.EqualTo(0));
            Assert.That(Convert.ToInt32(result["keptCount"]), Is.EqualTo(modificationCount));
            var remaining = PrefabUtility.GetPropertyModifications(variant);
            Assert.That(remaining.Any(modification => modification.propertyPath == "m_Materials.Array.size"), Is.True);
            Assert.That(remaining.Any(modification => modification.propertyPath == "m_Materials.Array.data[0]"), Is.True);
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
        public void ComponentSetProperty_SetsInheritedBehaviourEnabled()
        {
            var gameObject = new GameObject("__UnityMCP_Disabled_UIDocument");
            try
            {
                var document = gameObject.AddComponent<UIDocument>();
                Assert.That(document.enabled, Is.True);

                object result = MCPComponentCommands.SetProperty(new Dictionary<string, object>
                {
                    { "path", gameObject.name },
                    { "componentType", typeof(UIDocument).FullName },
                    { "propertyName", "enabled" },
                    { "value", false },
                });

                PropertyInfo success = result.GetType().GetProperty("success");
                Assert.That(success, Is.Not.Null);
                Assert.That(success.GetValue(result), Is.EqualTo(true));
                Assert.That(document.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
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
        public void ToolMetadata_FirstClassSchemasOmitCompatibilityAliasesAndFalseAnnotations()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: true, includeSchema: true, limit: 200));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            string json = MiniJson.Serialize(tools);

            Assert.That(json, Does.Not.Contain("Alias for"));
            Assert.That(json, Does.Not.Contain("\"uidocumentInstanceId\""));
            foreach (var tool in tools)
            {
                var annotations = RequireDictionary(tool["annotations"]);
                Assert.That(annotations.ContainsKey("title"), Is.False);
                Assert.That(annotations.Values.OfType<bool>().All(value => value), Is.True);
            }
        }

        [Test]
        public void ToolMetadata_ValueSchemasAcceptPrimitiveNumbers()
        {
            MethodInfo getSchema = typeof(MCPToolMetadata).GetMethod("GetToolInputSchema",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(getSchema, Is.Not.Null);

            foreach (string route in new[]
                     {
                         "prefab-asset/set-property",
                         "serialized-object/set",
                         "component/set-property",
                         "localization/upsert-variable",
                     })
            {
                var schema = RequireDictionary(getSchema.Invoke(null, new object[] { route }));
                var properties = RequireDictionary(schema["properties"]);
                var valueSchema = RequireDictionary(properties["value"]);
                Assert.That(valueSchema.ContainsKey("type"), Is.False,
                    $"{route} must allow primitive JSON values such as 0.72.");
            }

            var toolsResult = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 500));
            var tools = (List<Dictionary<string, object>>)toolsResult["tools"];
            Assert.That(tools.Any(tool => tool["route"].ToString() == "component/set-property"), Is.True);
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

        private static void CreateTestPrefab(bool addCollider = false, bool addRenderer = false,
            bool addParticleReference = false)
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

                Component referenceTarget = null;
                if (addCollider)
                {
                    var collider = source.AddComponent<BoxCollider>();
                    collider.center = new Vector3(1, 2, 3);
                    collider.size = new Vector3(4, 5, 6);
                    collider.isTrigger = true;
                    referenceTarget = collider;
                }

                if (addRenderer)
                    source.AddComponent<MeshRenderer>();

                if (addParticleReference)
                {
                    Assert.That(referenceTarget, Is.Not.Null);
                    var referenceOwner = new GameObject("Reference Owner");
                    referenceOwner.transform.SetParent(root.transform, false);
                    var particleSystem = referenceOwner.AddComponent<ParticleSystem>();
                    var trigger = particleSystem.trigger;
                    trigger.enabled = true;
                    trigger.SetCollider(0, referenceTarget);
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

        private static string GetYamlObjectBlockContaining(string assetPath, string marker)
        {
            string text = File.ReadAllText(GetAbsolutePath(assetPath)).Replace("\r\n", "\n").Replace('\r', '\n');
            var matches = Regex.Matches(text, @"(?m)^--- !u!\d+ &-?\d+(?: stripped)?[\t ]*$");
            for (int index = 0; index < matches.Count; index++)
            {
                int end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
                string block = text.Substring(matches[index].Index, end - matches[index].Index);
                if (block.Contains(marker))
                    return block;
            }

            Assert.Fail($"Could not find YAML object block containing '{marker}'.");
            return "";
        }

        private static void RewriteYamlObjectBlock(string assetPath, string marker,
            Func<string, string> rewrite)
        {
            string absolutePath = GetAbsolutePath(assetPath);
            string text = File.ReadAllText(absolutePath).Replace("\r\n", "\n").Replace('\r', '\n');
            string block = GetYamlObjectBlockContaining(assetPath, marker);
            string rewritten = rewrite(block);
            Assert.That(rewritten, Is.Not.EqualTo(block));
            File.WriteAllText(absolutePath, text.Replace(block, rewritten));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static string GetAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
    }
}
