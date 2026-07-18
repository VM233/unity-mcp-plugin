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
    public enum ScalarEnvelopeTestMode
    {
        First,
        Second
    }

    [Serializable]
    public sealed class ScalarEnvelopeTestConfig
    {
        public ScalarEnvelopeTestMode mode;
    }

    public sealed class ScalarEnvelopeTestObject : ScriptableObject
    {
        public ScalarEnvelopeTestConfig config = new ScalarEnvelopeTestConfig();
    }

    public sealed class MCPCommandRegressionTests
    {
        private const string TEST_FOLDER = "Assets/__UnityMCPTests";
        private const string PREFAB_PATH = TEST_FOLDER + "/MCP Test Prefab.prefab";
        private const string RUNTIME_MUTATION_TOOL_NAME = "unity-mcp-tests/set-runtime-state";
        private const string LAZY_READ_TOOL_NAME = "unity-mcp-tests/read-lazy-state";

        [MCPProjectTool(RUNTIME_MUTATION_TOOL_NAME,
            Description = "Regression fixture for explicit runtime mutation metadata.",
            InputSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            MutatesRuntime = true,
            RequiresPlayMode = true,
            FirstClass = true)]
        private static object SetRuntimeStateFixture(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "receivedKeys", args.Keys.OrderBy(key => key).ToArray() }
            };
        }

        [MCPProjectTool(LAZY_READ_TOOL_NAME,
            Description = "Regression fixture for an explicitly lazy project tool.",
            InputSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            ReadOnly = true)]
        private static object ReadLazyStateFixture(Dictionary<string, object> args)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "receivedKeys", args.Keys.OrderBy(key => key).ToArray() }
            };
        }

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

        [Test]
        public void TransactionEdit_ArrayInsert_PersistsPureYamlListAddition()
        {
            CreateTestPrefab();
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var component = root.transform.Find("Source").gameObject.AddComponent<Animation>();
                var serialized = new SerializedObject(component);
                var animations = serialized.FindProperty("m_Animations");
                Assert.That(animations, Is.Not.Null);
                animations.arraySize = 1;
                animations.GetArrayElementAtIndex(0).objectReferenceValue = null;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH), Is.Not.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            var result = RequireDictionary(MCPPrefabAssetCommands.TransactionEdit(
                new Dictionary<string, object>
                {
                    { "assetPath", PREFAB_PATH },
                    { "operations", new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "arrayInsert" },
                                { "prefabPath", "Source" },
                                { "componentType", typeof(Animation).FullName },
                                { "propertyName", "m_Animations" },
                                { "index", 1 },
                                { "value", null },
                            },
                        }
                    },
                }));

            Assert.That(result["success"], Is.EqualTo(true), MiniJson.Serialize(result));
            root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var component = root.transform.Find("Source").GetComponent<Animation>();
                var animations = new SerializedObject(component).FindProperty("m_Animations");
                Assert.That(animations.arraySize, Is.EqualTo(2),
                    "The transaction must not report success if YAML stabilization discards the array insertion.");
                Assert.That(animations.GetArrayElementAtIndex(1).objectReferenceValue, Is.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [UnityTest]
        public IEnumerator TransactionEditDeferred_LoadedComponentTypes_DoNotScheduleAssetRefresh()
        {
            CreateTestPrefab();
            FieldInfo refreshScheduledField = typeof(MCPPrefabAssetCommands).GetField("_assetRefreshScheduled",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(refreshScheduledField, Is.Not.Null);
            refreshScheduledField.SetValue(null, false);

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
                } },
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
                } },
            }, result => completedResult = result, _ => { });

            Assert.That(refreshScheduledField.GetValue(null), Is.EqualTo(false),
                "Loaded component types must not schedule the missing-type asset refresh path.");

            double timeoutAt = EditorApplication.timeSinceStartup + 5d;
            while (completedResult == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            Assert.That(completedResult, Is.Not.Null);
            var result = RequireDictionary(completedResult);
            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(result.TryGetValue("errorCode", out object errorCode) &&
                        Equals(errorCode, "asset_refresh_scheduled"), Is.False);
            Assert.That(refreshScheduledField.GetValue(null), Is.EqualTo(false));
        }

        [UnityTest]
        public IEnumerator ConfigureComponentDeferred_AddsConfiguresReferencesAndThenUpdatesInPlace()
        {
            CreateTestPrefab();
            var renderTexture = new RenderTexture(16, 16, 0);
            string renderTexturePath = TEST_FOLDER + "/Configured Camera Target.asset";
            AssetDatabase.CreateAsset(renderTexture, renderTexturePath);

            object completedResult = null;
            MCPPrefabAssetCommands.ConfigureComponentDeferred(new Dictionary<string, object>
            {
                { "assetPath", PREFAB_PATH },
                { "prefabPath", "Target" },
                { "componentType", typeof(Camera).FullName },
                { "typeResolveStableMs", 0 },
                { "properties", new Dictionary<string, object> { { "m_Depth", 7f } } },
                { "references", new List<object>
                {
                    new Dictionary<string, object>
                    {
                        { "propertyName", "m_TargetTexture" },
                        { "referenceAssetPath", renderTexturePath },
                    },
                } },
            }, result => completedResult = result, _ => { });

            double timeoutAt = EditorApplication.timeSinceStartup + 5d;
            while (completedResult == null && EditorApplication.timeSinceStartup < timeoutAt)
                yield return null;

            Assert.That(completedResult, Is.Not.Null);
            var result = RequireDictionary(completedResult);
            Assert.That(result["success"], Is.EqualTo(true));
            var summaries = (List<Dictionary<string, object>>)result["operationSummaries"];
            Assert.That(summaries.Single()["type"], Is.EqualTo("configureComponent"));
            Assert.That(summaries.Single()["added"], Is.EqualTo(true));
            Assert.That((List<Dictionary<string, object>>)summaries.Single()["references"], Has.Count.EqualTo(1));

            var updateResult = RequireDictionary(MCPPrefabAssetCommands.ConfigureComponent(
                new Dictionary<string, object>
                {
                    { "assetPath", PREFAB_PATH },
                    { "prefabPath", "Target" },
                    { "componentType", typeof(Camera).FullName },
                    { "properties", new Dictionary<string, object> { { "m_Depth", 9f } } },
                }));
            Assert.That(updateResult["success"], Is.EqualTo(true));
            var updateSummaries = (List<Dictionary<string, object>>)updateResult["operationSummaries"];
            Assert.That(updateSummaries.Single()["added"], Is.EqualTo(false));

            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                var cameras = root.transform.Find("Target").GetComponents<Camera>();
                Assert.That(cameras, Has.Length.EqualTo(1));
                Assert.That(cameras[0].depth, Is.EqualTo(9f));
                Assert.That(cameras[0].targetTexture, Is.EqualTo(renderTexture));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void UnifiedExecutionRoutes_AreDeferredAndLegacyBatchRoutesAreRemoved()
        {
            var routesProperty = typeof(MCPBridgeServer).GetProperty("DeferredRouteNames",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(routesProperty, Is.Not.Null);
            var routes = (IEnumerable<string>)routesProperty.GetValue(null);
            Assert.That(routes, Does.Contain("prefab-asset/configure-component"));
            Assert.That(routes, Does.Contain("prefab-asset/transaction-edit"));
            Assert.That(routes, Does.Contain("asset/import"));
            Assert.That(routes, Does.Contain("asset/move"));
            Assert.That(routes, Does.Contain("component/set-reference"));
            Assert.That(routes, Does.Contain("localization/upsert-entry"));
            Assert.That(routes, Does.Contain("editor/play-mode"));
            Assert.That(routes, Does.Contain("profiler/memory-snapshot"));

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
        public void RequestQueue_CleanupIsTimeBasedAndSkipsUnchangedSnapshotWrites()
        {
            var cleanupIntervalField = typeof(MCPRequestQueue).GetField("CleanupIntervalSeconds",
                BindingFlags.Static | BindingFlags.NonPublic);
            var nextCleanupField = typeof(MCPRequestQueue).GetField("_nextCleanupAt",
                BindingFlags.Static | BindingFlags.NonPublic);
            var cleanupMethod = typeof(MCPRequestQueue).GetMethod("RunCleanup",
                BindingFlags.Static | BindingFlags.NonPublic);
            var completedField = typeof(MCPRequestQueue).GetField("_completedTickets",
                BindingFlags.Static | BindingFlags.NonPublic);
            var queueLockField = typeof(MCPRequestQueue).GetField("_queueLock",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(cleanupIntervalField, Is.Not.Null);
            Assert.That((double)cleanupIntervalField.GetRawConstantValue(), Is.GreaterThanOrEqualTo(5d));
            Assert.That(nextCleanupField, Is.Not.Null);
            Assert.That(typeof(MCPRequestQueue).GetField("_frameTick",
                BindingFlags.Static | BindingFlags.NonPublic), Is.Null,
                "Ticket cleanup must not scale with Editor frame rate.");
            Assert.That(cleanupMethod, Is.Not.Null);
            Assert.That(completedField, Is.Not.Null);
            Assert.That(queueLockField, Is.Not.Null);

            long ticketId = DateTime.UtcNow.Ticks;
            var completed = (Dictionary<long, MCPRequestQueue.RequestTicket>)completedField.GetValue(null);
            object queueLock = queueLockField.GetValue(null);
            lock (queueLock)
            {
                completed[ticketId] = new MCPRequestQueue.RequestTicket
                {
                    TicketId = ticketId,
                    AgentId = "cleanup-regression",
                    ActionName = "editor/ping",
                    Status = MCPRequestQueue.RequestStatus.Completed,
                    SubmittedAt = DateTime.UtcNow.AddMinutes(-20),
                    CompletedAt = DateTime.UtcNow.AddMinutes(-20),
                };
            }

            try
            {
                Assert.That(cleanupMethod.Invoke(null, null), Is.EqualTo(true));
                lock (queueLock)
                    Assert.That(completed.ContainsKey(ticketId), Is.False);
                Assert.That(cleanupMethod.Invoke(null, null), Is.EqualTo(false),
                    "A cleanup pass with no newly expired tickets must not rewrite the persistent snapshot.");
            }
            finally
            {
                lock (queueLock)
                    completed.Remove(ticketId);
            }
        }

        [Test]
        public void EditorIdleWait_IsClassifiedAsReadOperation()
        {
            var method = typeof(MCPRequestQueue).GetMethod("IsReadOperation",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { "wait/editor-idle" }), Is.EqualTo(true));
        }

        [UnityTest]
        public IEnumerator EditorIdleWait_DuplicateRequestsReuseTheGlobalActiveTicket()
        {
            var arguments = new Dictionary<string, object>
            {
                { "timeoutMs", 5000 },
                { "stableFrames", 1 },
                { "stableMs", 0 },
            };
            var first = MCPRequestQueue.SubmitResumableEditorIdleWait(
                "idle-wait-first-" + Guid.NewGuid(), arguments, out bool firstReused);
            var second = MCPRequestQueue.SubmitResumableEditorIdleWait(
                "idle-wait-replay-" + Guid.NewGuid(), arguments, out bool secondReused);

            Assert.That(firstReused, Is.False);
            Assert.That(secondReused, Is.True);
            Assert.That(second.TicketId, Is.EqualTo(first.TicketId));

            double timeoutAt = EditorApplication.timeSinceStartup + 5d;
            while (first.Status != MCPRequestQueue.RequestStatus.Completed &&
                   first.Status != MCPRequestQueue.RequestStatus.Failed &&
                   EditorApplication.timeSinceStartup < timeoutAt)
            {
                MCPRequestQueue.ProcessNextRequests(5);
                yield return null;
            }

            Assert.That(first.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.Completed));
            var result = RequireDictionary(first.Result);
            Assert.That(result["success"], Is.EqualTo(true));
            Assert.That(result["resumedAfterReload"], Is.EqualTo(false));
        }

        [Test]
        public void EditorIdleWait_ExecutingSnapshotRestoresSameTicketWithRemainingDeadline()
        {
            long ticketId = DateTime.UtcNow.Ticks;
            var persistentArguments = new Dictionary<string, object>
            {
                { "timeoutMs", 5000 },
                { "stableFrames", 3 },
                { "stableMs", 500 },
                { "_resumeCount", 0 },
            };
            var snapshot = new Dictionary<string, object>
            {
                { "ticketId", ticketId },
                { "agentId", "reload-regression" },
                { "actionName", "wait/editor-idle" },
                { "status", MCPRequestQueue.RequestStatus.Executing.ToString() },
                { "queuePosition", 0 },
                { "submittedAt", DateTime.UtcNow.AddMilliseconds(-100).ToString("O") },
                { "requestKey", "wait/editor-idle|5000|3|500" },
                { "persistentArguments", persistentArguments },
                { "resumeCount", 0 },
            };

            var method = typeof(MCPRequestQueue).GetMethod("TryRestoreEditorIdleWait",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var invokeArguments = new object[] { snapshot, null };
            Assert.That(method.Invoke(null, invokeArguments), Is.EqualTo(true));

            var restored = (MCPRequestQueue.RequestTicket)invokeArguments[1];
            Assert.That(restored.TicketId, Is.EqualTo(ticketId));
            Assert.That(restored.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.Queued));
            Assert.That(restored.ResumeCount, Is.EqualTo(1));
            Assert.That(Convert.ToInt32(restored.PersistentArguments["timeoutMs"]),
                Is.InRange(1, 5000));
            Assert.That(Convert.ToInt32(restored.PersistentArguments["_resumeCount"]), Is.EqualTo(1));
            Assert.That(restored.PersistentArguments.ContainsKey("_deadlineUtc"), Is.True);

            var deferredProperty = typeof(MCPRequestQueue.RequestTicket).GetProperty(
                "ProgressiveDeferredAction", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(deferredProperty, Is.Not.Null);
            Assert.That(deferredProperty.GetValue(restored), Is.Not.Null);
        }

        [Test]
        public void EditorIdleWait_CompletedSnapshotRestoresTerminalResult()
        {
            var completedResult = new Dictionary<string, object>
            {
                { "success", true },
                { "timedOut", false },
                { "resumeCount", 1 },
            };
            var snapshot = new Dictionary<string, object>
            {
                { "ticketId", DateTime.UtcNow.Ticks },
                { "agentId", "reload-regression" },
                { "actionName", "wait/editor-idle" },
                { "status", MCPRequestQueue.RequestStatus.Completed.ToString() },
                { "submittedAt", DateTime.UtcNow.AddSeconds(-1).ToString("O") },
                { "completedAt", DateTime.UtcNow.ToString("O") },
                { "result", completedResult },
            };

            var method = typeof(MCPRequestQueue).GetMethod("TryRestoreEditorIdleWait",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var invokeArguments = new object[] { snapshot, null };
            Assert.That(method.Invoke(null, invokeArguments), Is.EqualTo(true));

            var restored = (MCPRequestQueue.RequestTicket)invokeArguments[1];
            Assert.That(restored.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.Completed));
            Assert.That(RequireDictionary(restored.Result)["success"], Is.EqualTo(true));
        }

        [Test]
        public void RequestQueue_CompletionBeforeWaiterRegistrationReturnsImmediately()
        {
            long ticketId = DateTime.UtcNow.Ticks;
            var expected = new Dictionary<string, object> { { "success", true } };
            var ticket = new MCPRequestQueue.RequestTicket
            {
                TicketId = ticketId,
                AgentId = "waiter-race-regression",
                ActionName = "wait/editor-idle",
                Status = MCPRequestQueue.RequestStatus.Completed,
                SubmittedAt = DateTime.UtcNow,
                CompletedAt = DateTime.UtcNow,
                Result = expected,
            };
            var completedField = typeof(MCPRequestQueue).GetField("_completedTickets",
                BindingFlags.Static | BindingFlags.NonPublic);
            var queueLockField = typeof(MCPRequestQueue).GetField("_queueLock",
                BindingFlags.Static | BindingFlags.NonPublic);
            var waitMethod = typeof(MCPRequestQueue).GetMethod("WaitForTicket",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(completedField, Is.Not.Null);
            Assert.That(queueLockField, Is.Not.Null);
            Assert.That(waitMethod, Is.Not.Null);
            var completed = (Dictionary<long, MCPRequestQueue.RequestTicket>)completedField.GetValue(null);
            object queueLock = queueLockField.GetValue(null);

            try
            {
                lock (queueLock)
                    completed[ticketId] = ticket;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var actual = waitMethod.Invoke(null, new object[] { ticket });
                stopwatch.Stop();

                Assert.That(actual, Is.SameAs(expected));
                Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(1000));
            }
            finally
            {
                lock (queueLock)
                    completed.Remove(ticketId);
            }
        }

        [Test]
        public void RequestTicketSnapshots_AreAtomicallyReplacedWithValidBackup()
        {
            string path = Path.Combine(Path.GetTempPath(),
                "unity-mcp-ticket-snapshot-" + Guid.NewGuid().ToString("N") + ".json");
            var writeMethod = typeof(MCPRequestQueue).GetMethod("WriteTextAtomically",
                BindingFlags.Static | BindingFlags.NonPublic);
            var readMethod = typeof(MCPRequestQueue).GetMethod("TryReadValidSnapshotJson",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(writeMethod, Is.Not.Null);
            Assert.That(readMethod, Is.Not.Null);

            try
            {
                writeMethod.Invoke(null, new object[] { path, "[{\"generation\":1}]" });
                writeMethod.Invoke(null, new object[] { path, "[{\"generation\":2}]" });

                var mainReadArguments = new object[] { path, null };
                var backupReadArguments = new object[] { path + ".bak", null };
                Assert.That(readMethod.Invoke(null, mainReadArguments), Is.EqualTo(true));
                Assert.That(readMethod.Invoke(null, backupReadArguments), Is.EqualTo(true));
                Assert.That(mainReadArguments[1].ToString(), Does.Contain("\"generation\":2"));
                Assert.That(backupReadArguments[1].ToString(), Does.Contain("\"generation\":1"));
            }
            finally
            {
                foreach (string candidate in new[] { path, path + ".bak", path + ".tmp" })
                {
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
            }
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
        public void AssetRefresh_FullRefreshReconcilesExternalDeletion()
        {
            const string deletedPath = TEST_FOLDER + "/Externally Deleted.txt";
            const string importedPath = TEST_FOLDER + "/Imported First.txt";
            File.WriteAllText(GetAbsolutePath(deletedPath), "delete me");
            File.WriteAllText(GetAbsolutePath(importedPath), "keep me");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.AssetPathToGUID(deletedPath), Is.Not.Empty);

            File.Delete(GetAbsolutePath(deletedPath));
            File.Delete(GetAbsolutePath(deletedPath) + ".meta");

            var method = typeof(MCPAssetCommands).GetMethod("ExecuteRefreshImmediate",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var response = RequireDictionary(method.Invoke(null, new object[]
            {
                new Dictionary<string, object>
                {
                    { "forceUpdate", true },
                },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["refreshMode"], Is.EqualTo("full"));
            Assert.That(response["refreshedAllAssets"], Is.EqualTo(true));
            Assert.That(((List<string>)response["importedPaths"]).Count, Is.Zero);
            Assert.That(File.Exists(GetAbsolutePath(deletedPath)), Is.False);
            Assert.That(File.Exists(GetAbsolutePath(deletedPath) + ".meta"), Is.False);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(deletedPath), Is.Null);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(deletedPath), Is.Null);
        }

        [Test]
        public void AssetRefresh_TargetedImportDoesNotScanUnrelatedExternalChanges()
        {
            const string deletedPath = TEST_FOLDER + "/Unrelated Deleted.txt";
            const string importedPath = TEST_FOLDER + "/Targeted Import.txt";
            File.WriteAllText(GetAbsolutePath(deletedPath), "delete me externally");
            File.WriteAllText(GetAbsolutePath(importedPath), "import me");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(deletedPath), Is.Not.Null);

            File.Delete(GetAbsolutePath(deletedPath));
            File.Delete(GetAbsolutePath(deletedPath) + ".meta");

            var method = typeof(MCPAssetCommands).GetMethod("ExecuteRefreshImmediate",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var response = RequireDictionary(method.Invoke(null, new object[]
            {
                new Dictionary<string, object>
                {
                    { "assetPaths", new[] { importedPath } },
                    { "forceUpdate", true },
                },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["refreshMode"], Is.EqualTo("targeted"));
            Assert.That(response["refreshedAllAssets"], Is.EqualTo(false));
            CollectionAssert.AreEqual(new[] { importedPath },
                (List<string>)response["importedPaths"]);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(deletedPath), Is.Not.Null,
                "A targeted import must not reconcile unrelated external changes.");
        }

        [Test]
        public void AssetRefresh_TargetedCompilationAssetsDoNotUseForceUpdate()
        {
            MethodInfo getImportOptions = typeof(MCPAssetCommands).GetMethod(
                "GetTargetedImportOptions", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo getSkippedPaths = typeof(MCPAssetCommands).GetMethod(
                "GetTargetedForceUpdateSkippedPaths", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(getImportOptions, Is.Not.Null);
            Assert.That(getSkippedPaths, Is.Not.Null);

            ImportAssetOptions scriptOptions = (ImportAssetOptions)getImportOptions.Invoke(null,
                new object[] { "Assets/Scripts/Changed.cs", true });
            ImportAssetOptions assemblyOptions = (ImportAssetOptions)getImportOptions.Invoke(null,
                new object[] { "Assets/Scripts/Game.asmdef", true });
            ImportAssetOptions styleOptions = (ImportAssetOptions)getImportOptions.Invoke(null,
                new object[] { "Assets/UI/Changed.uss", true });

            Assert.That(scriptOptions.HasFlag(ImportAssetOptions.ForceSynchronousImport), Is.True);
            Assert.That(scriptOptions.HasFlag(ImportAssetOptions.ForceUpdate), Is.False);
            Assert.That(assemblyOptions.HasFlag(ImportAssetOptions.ForceUpdate), Is.False);
            Assert.That(styleOptions.HasFlag(ImportAssetOptions.ForceUpdate), Is.True);

            CollectionAssert.AreEqual(
                new[] { "Assets/Scripts/Changed.cs", "Assets/Scripts/Game.asmdef" },
                (List<string>)getSkippedPaths.Invoke(null, new object[]
                {
                    new[]
                    {
                        "Assets/Scripts/Changed.cs",
                        "Assets/UI/Changed.uss",
                        "Assets/Scripts/Game.asmdef",
                    },
                    true,
                }));
        }

        [Test]
        public void AssetRefresh_TargetedImportsOrderDependenciesBeforeDependents()
        {
            const string ussPath = TEST_FOLDER + "/Refresh Dependency.uss";
            const string uxmlPath = TEST_FOLDER + "/Refresh Dependent.uxml";
            File.WriteAllText(GetAbsolutePath(ussPath), ".refresh-dependency { color: red; }");
            AssetDatabase.ImportAsset(ussPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            string ussGuid = AssetDatabase.AssetPathToGUID(ussPath);
            Assert.That(ussGuid, Is.Not.Empty);

            File.WriteAllText(GetAbsolutePath(uxmlPath),
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">" +
                $"<Style src=\"project://database/{ussPath.Replace(" ", "%20")}?fileID=7433441132597879392&amp;guid={ussGuid}&amp;type=3#Refresh%20Dependency\"/>" +
                "<ui:VisualElement class=\"refresh-dependency\"/>" +
                "</ui:UXML>");
            AssetDatabase.ImportAsset(uxmlPath,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            CollectionAssert.Contains(AssetDatabase.GetDependencies(uxmlPath, false), ussPath);

            File.WriteAllText(GetAbsolutePath(ussPath), ".refresh-dependency { color: blue; }");
            File.SetLastWriteTimeUtc(GetAbsolutePath(ussPath),
                File.GetLastWriteTimeUtc(GetAbsolutePath(ussPath)).AddSeconds(10));

            var method = typeof(MCPAssetCommands).GetMethod("ExecuteRefreshImmediate",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var response = RequireDictionary(method.Invoke(null, new object[]
            {
                new Dictionary<string, object>
                {
                    { "assetPaths", new[] { uxmlPath, ussPath } },
                    { "forceUpdate", true },
                },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            Assert.That(response["refreshMode"], Is.EqualTo("targeted"));
            Assert.That(response["refreshedAllAssets"], Is.EqualTo(false));
            CollectionAssert.AreEqual(new[] { ussPath, uxmlPath },
                (List<string>)response["importedPaths"]);
            Assert.That(AssetDatabase.LoadAssetAtPath<StyleSheet>(ussPath), Is.Not.Null);
            Assert.That(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath), Is.Not.Null);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void ToolMetadata_ExposesPersistentPlayerBuildAndAssetRefreshJobs()
        {
            var toolsResult = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 500));
            var tools = (List<Dictionary<string, object>>)toolsResult["tools"];

            foreach (string route in new[]
                     {
                         "build/run-test",
                         "build/get-job",
                         "asset/refresh",
                         "asset/get-refresh-job",
                     })
            {
                var tool = tools.Single(item => item["route"].ToString() == route);
                Assert.That(tool["firstClass"], Is.EqualTo(true), route);
                Assert.That(tool["inputSchema"], Is.InstanceOf<Dictionary<string, object>>(), route);
            }

            var refreshTool = tools.Single(item => item["route"].ToString() == "asset/refresh");
            var refreshSchema = RequireDictionary(refreshTool["inputSchema"]);
            var refreshProperties = RequireDictionary(refreshSchema["properties"]);
            Assert.That(refreshProperties.ContainsKey("assetPaths"), Is.True);
            Assert.That(refreshProperties.ContainsKey("reconcileExternalChanges"), Is.False);
            Assert.That(refreshProperties.ContainsKey("expectedProjectPath"), Is.True);

            var refreshJobTool = tools.Single(item => item["route"].ToString() == "asset/get-refresh-job");
            var refreshJobSchema = RequireDictionary(refreshJobTool["inputSchema"]);
            var refreshJobProperties = RequireDictionary(refreshJobSchema["properties"]);
            Assert.That(refreshJobProperties.ContainsKey("timeoutMs"), Is.True);

            foreach (var tool in tools.Where(item => !(bool)item["readOnly"]))
            {
                var schema = RequireDictionary(tool["inputSchema"]);
                var properties = RequireDictionary(schema["properties"]);
                Assert.That(properties.ContainsKey("expectedProjectPath"), Is.True,
                    tool["route"].ToString());
            }
        }

        [Test]
        public void ToolMetadata_ExposesPlayModeAndProfilerRoutesAsFirstClass()
        {
            var toolsResult = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 500));
            var tools = (List<Dictionary<string, object>>)toolsResult["tools"];
            string[] expectedRoutes =
            {
                "editor/play-mode",
                "profiler/enable",
                "profiler/stats",
                "profiler/memory",
                "profiler/frame-data",
                "profiler/analyze",
                "profiler/memory-status",
                "profiler/memory-breakdown",
                "profiler/memory-top-assets",
                "profiler/memory-snapshot",
                "profiler/memory-snapshot-status",
            };

            foreach (string route in expectedRoutes)
            {
                var tool = tools.Single(item => item["route"].ToString() == route);
                Assert.That(tool["firstClass"], Is.EqualTo(true), route);
                Assert.That(tool["inputSchema"], Is.InstanceOf<Dictionary<string, object>>(), route);
                Assert.That(tool["toolName"], Is.EqualTo("unity_" + route.Replace('/', '_').Replace('-', '_')),
                    route);
            }

            var playModeTool = tools.Single(item => item["route"].ToString() == "editor/play-mode");
            var playModeSchema = RequireDictionary(playModeTool["inputSchema"]);
            var playModeProperties = RequireDictionary(playModeSchema["properties"]);
            Assert.That(playModeProperties.Keys, Does.Contain("action"));
            Assert.That(playModeProperties.Keys, Does.Contain("timeoutMs"));
            Assert.That(playModeProperties.Keys, Does.Contain("expectedProjectPath"));

            var snapshotStatusTool = tools.Single(item =>
                item["route"].ToString() == "profiler/memory-snapshot-status");
            Assert.That(snapshotStatusTool["readOnly"], Is.EqualTo(true));
            var snapshotStatusSchema = RequireDictionary(snapshotStatusTool["inputSchema"]);
            var snapshotStatusProperties = RequireDictionary(snapshotStatusSchema["properties"]);
            Assert.That(snapshotStatusProperties.Keys, Does.Contain("jobId"));
        }

        [Test]
        public void MemorySnapshot_UsesCurrentProfilerApiWhenAvailable()
        {
            var resolver = typeof(MCPMemoryProfilerCommands).GetMethod("ResolveMemoryProfilerType",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(resolver, Is.Not.Null);
            var profilerType = resolver.Invoke(null, null) as Type;
            Assert.That(profilerType, Is.Not.Null);
#if UNITY_2022_2_OR_NEWER
            Assert.That(profilerType.FullName, Is.EqualTo("Unity.Profiling.Memory.MemoryProfiler"));
#else
            Assert.That(profilerType.FullName, Does.Contain("MemoryProfiler"));
#endif
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
        public void UIBuilderPreviewVisualAnalysis_RejectsShellAndCheckerboardOnly()
        {
            const int width = 128;
            const int height = 128;
            var canvasRect = new RectInt(8, 8, 112, 112);
            var documentRect = new RectInt(32, 32, 64, 64);
            var pixels = CreateTopLeftCheckerboard(width, height, canvasRect,
                new Color32(82, 82, 82, 255), new Color32(98, 98, 98, 255));

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (x < canvasRect.xMin || x >= canvasRect.xMax ||
                        y < canvasRect.yMin || y >= canvasRect.yMax)
                    {
                        SetTopLeftPixel(pixels, width, height, x, y, new Color32(180, 40, 40, 255));
                    }
                }
            }

            var analysis = InvokeUIBuilderPixelAnalysis(pixels, width, height, documentRect, canvasRect);

            Assert.That(analysis["conclusive"], Is.EqualTo(true));
            Assert.That(analysis["visualValid"], Is.EqualTo(false));
            Assert.That(analysis["documentVisuallyBlank"], Is.EqualTo(true));
            Assert.That(analysis["reason"], Is.EqualTo("document_matches_canvas_background"));
        }

        [Test]
        public void UIBuilderPreviewVisualAnalysis_AcceptsDocumentColorsOutsideCanvasPalette()
        {
            const int width = 128;
            const int height = 128;
            var canvasRect = new RectInt(8, 8, 112, 112);
            var documentRect = new RectInt(32, 32, 64, 64);
            var pixels = CreateTopLeftCheckerboard(width, height, canvasRect,
                new Color32(82, 82, 82, 255), new Color32(98, 98, 98, 255));
            FillTopLeftRect(pixels, width, height, documentRect, new Color32(132, 74, 39, 255));

            var analysis = InvokeUIBuilderPixelAnalysis(pixels, width, height, documentRect, canvasRect);

            Assert.That(analysis["visualValid"], Is.EqualTo(true));
            Assert.That(analysis["hasOutOfPaletteEvidence"], Is.EqualTo(true));
            Assert.That(analysis["reason"], Is.EqualTo("document_contains_visual_content"));
        }

        [Test]
        public void UIBuilderPreviewVisualAnalysis_AcceptsSolidDocumentUsingCheckerboardColor()
        {
            const int width = 128;
            const int height = 128;
            var canvasRect = new RectInt(8, 8, 112, 112);
            var documentRect = new RectInt(32, 32, 64, 64);
            var firstCheckerColor = new Color32(82, 82, 82, 255);
            var pixels = CreateTopLeftCheckerboard(width, height, canvasRect, firstCheckerColor,
                new Color32(98, 98, 98, 255));
            FillTopLeftRect(pixels, width, height, documentRect, firstCheckerColor);

            var analysis = InvokeUIBuilderPixelAnalysis(pixels, width, height, documentRect, canvasRect);

            Assert.That(analysis["visualValid"], Is.EqualTo(true));
            Assert.That(analysis["hasOutOfPaletteEvidence"], Is.EqualTo(false));
            Assert.That(analysis["hasDistributionEvidence"], Is.EqualTo(true));
        }

        [Test]
        public void UIBuilderPreviewVisualAnalysis_RejectsCheckerboardWhenCanvasEqualsDocument()
        {
            const int width = 128;
            const int height = 128;
            var documentAndCanvasRect = new RectInt(8, 8, 112, 112);
            var pixels = CreateTopLeftCheckerboard(width, height, documentAndCanvasRect,
                new Color32(130, 130, 130, 255), new Color32(146, 146, 146, 255));

            var analysis = InvokeUIBuilderPixelAnalysis(pixels, width, height, documentAndCanvasRect,
                documentAndCanvasRect);

            Assert.That(analysis["conclusive"], Is.EqualTo(true));
            Assert.That(analysis["backgroundComparable"], Is.EqualTo(false));
            Assert.That(analysis["visualValid"], Is.EqualTo(false));
            Assert.That(analysis["reason"], Is.EqualTo("document_matches_checkerboard_or_blank_shell"));
        }

        [Test]
        public void UIBuilderPreviewVisualAnalysis_AcceptsColoredContentWhenCanvasEqualsDocument()
        {
            const int width = 128;
            const int height = 128;
            var documentAndCanvasRect = new RectInt(8, 8, 112, 112);
            var pixels = CreateTopLeftCheckerboard(width, height, documentAndCanvasRect,
                new Color32(130, 130, 130, 255), new Color32(146, 146, 146, 255));
            FillTopLeftRect(pixels, width, height, new RectInt(24, 24, 80, 80),
                new Color32(132, 74, 39, 255));

            var analysis = InvokeUIBuilderPixelAnalysis(pixels, width, height, documentAndCanvasRect,
                documentAndCanvasRect);

            Assert.That(analysis["visualValid"], Is.EqualTo(true));
            Assert.That(analysis["hasTargetColorEvidence"], Is.EqualTo(true));
            Assert.That(analysis["reason"], Is.EqualTo("document_contains_visual_content"));
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
        public void GameViewScreenshot_RenderTextureWriterScalesAndWritesDecodablePng()
        {
            const string screenshotPath = TEST_FOLDER + "/Paused Game View.png";
            string fullPath = GetAbsolutePath(screenshotPath);
            var renderTexture = new RenderTexture(8, 6, 0, RenderTextureFormat.ARGB32);
            Texture2D decoded = null;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                renderTexture.Create();
                RenderTexture.active = renderTexture;
                GL.Clear(true, true, Color.magenta);
                RenderTexture.active = previousActive;

                MethodInfo writer = typeof(MCPScreenshotCommands).GetMethod("WriteRenderTexturePng",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(writer, Is.Not.Null);
                var result = RequireDictionary(writer.Invoke(null,
                    new object[] { renderTexture, fullPath, 2, false }));

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["width"], Is.EqualTo(16));
                Assert.That(result["height"], Is.EqualTo(12));
                Assert.That(File.Exists(fullPath), Is.True);

                decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Assert.That(decoded.LoadImage(File.ReadAllBytes(fullPath)), Is.True);
                Color sample = decoded.GetPixel(decoded.width / 2, decoded.height / 2);
                Assert.That(sample.r, Is.GreaterThan(0.9f));
                Assert.That(sample.g, Is.LessThan(0.1f));
                Assert.That(sample.b, Is.GreaterThan(0.9f));
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (decoded != null)
                    Object.DestroyImmediate(decoded);
                renderTexture.Release();
                Object.DestroyImmediate(renderTexture);
            }
        }

        [Test]
        public void GameViewScreenshot_VerticalFlipSwapsPixelRows()
        {
            var bottomLeft = new Color32(255, 0, 0, 255);
            var bottomRight = new Color32(0, 255, 0, 255);
            var topLeft = new Color32(0, 0, 255, 255);
            var topRight = new Color32(255, 255, 255, 255);
            var pixels = new[] { bottomLeft, bottomRight, topLeft, topRight };

            MethodInfo flip = typeof(MCPScreenshotCommands).GetMethod("FlipPixelsVertically",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(flip, Is.Not.Null);
            flip.Invoke(null, new object[] { pixels, 2, 2 });

            Assert.That(pixels, Is.EqualTo(new[] { topLeft, topRight, bottomLeft, bottomRight }));
        }

        [Test]
        public void EditorWindowCapture_MapsPanelFromVirtualizedMultiMonitorCoordinates()
        {
            MethodInfo map = typeof(MCPScreenshotCommands).GetMethod("MapPanelRectToHostClientCapture",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(map, Is.Not.Null);

            var panelRect = new Rect(2126.6665f, 79f, 929f, 555f);
            var hostRect = new Rect(1706.6666f, 43f, 1920f, 989f);
            var clientRect = new RectInt(8, 51, 1920, 989);

            var mapped = (RectInt)map.Invoke(null, new object[] { panelRect, hostRect, clientRect });

            Assert.That(mapped, Is.EqualTo(new RectInt(428, 87, 929, 555)));
        }

        [Test]
        public void EditorWindowCapture_ScalesHostLocalCoordinatesForHighDpiClient()
        {
            MethodInfo map = typeof(MCPScreenshotCommands).GetMethod("MapPanelRectToHostClientCapture",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(map, Is.Not.Null);

            var panelRect = new Rect(-1180f, 50f, 600f, 400f);
            var hostRect = new Rect(-1280f, 0f, 1280f, 720f);
            var clientRect = new RectInt(8, 31, 1920, 1080);

            var mapped = (RectInt)map.Invoke(null, new object[] { panelRect, hostRect, clientRect });

            Assert.That(mapped, Is.EqualTo(new RectInt(158, 106, 900, 600)));
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
        public void RuntimeMutatingProjectTool_IsExplicitlyFirstClass()
        {
            var descriptor = MCPProjectToolCommands.GetToolDictionaries(validOnly: true)
                .Single(tool => tool["toolName"].ToString() == RUNTIME_MUTATION_TOOL_NAME);
            Assert.That(descriptor["readOnly"], Is.EqualTo(false));
            Assert.That(descriptor["mutatesAssets"], Is.EqualTo(false));
            Assert.That(descriptor["mutatesRuntime"], Is.EqualTo(true));
            Assert.That(descriptor["requiresPlayMode"], Is.EqualTo(true));

            var toolsResult = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 500));
            var tools = (List<Dictionary<string, object>>)toolsResult["tools"];
            var tool = tools.Single(item =>
                item["route"].ToString() == "project-tools/call/" + RUNTIME_MUTATION_TOOL_NAME);
            Assert.That(tool["firstClass"], Is.EqualTo(true));
            Assert.That(tool["mutatesRuntime"], Is.EqualTo(true));
            Assert.That(tool["exposure"], Is.EqualTo("first-class"));
        }

        [Test]
        public void ProjectTool_FirstClassExposureMustBeExplicit()
        {
            var descriptor = MCPProjectToolCommands.GetToolDictionaries(validOnly: true)
                .Single(tool => tool["toolName"].ToString() == LAZY_READ_TOOL_NAME);
            Assert.That(descriptor["readOnly"], Is.EqualTo(true));
            Assert.That(descriptor["firstClass"], Is.EqualTo(false));

            var toolsResult = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 500));
            var tools = (List<Dictionary<string, object>>)toolsResult["tools"];
            Assert.That(tools.Any(item =>
                item["route"].ToString() == "project-tools/call/" + LAZY_READ_TOOL_NAME), Is.False);
        }

        [Test]
        public void ProjectToolExecute_StripsProjectBindingArgumentsBeforeStrictSchemaValidation()
        {
            var response = RequireDictionary(MCPProjectToolCommands.Execute(new Dictionary<string, object>
            {
                { "toolName", LAZY_READ_TOOL_NAME },
                { "args", new Dictionary<string, object>
                    {
                        { "expectedProjectPath", "D:/UnityProjects/BattleIdle" },
                        { "expectedProjectName", "BattleIdle" },
                        { "targetProjectPath", "D:/UnityProjects/BattleIdle" },
                        { "targetProjectName", "BattleIdle" },
                        { "unityProjectPath", "D:/UnityProjects/BattleIdle" },
                        { "unityProjectName", "BattleIdle" },
                    }
                },
                { "expectedProjectPath", "D:/UnityProjects/BattleIdle" },
                { "expectedProjectName", "BattleIdle" },
            }));

            Assert.That(response["success"], Is.EqualTo(true));
            var result = RequireDictionary(response["result"]);
            CollectionAssert.IsEmpty((string[])result["receivedKeys"]);
        }

        [Test]
        public void ProjectToolDirectRoute_StripsProjectBindingArgumentsBeforeStrictSchemaValidation()
        {
            bool handled = MCPProjectToolCommands.TryExecuteDirectRoute(
                MCPProjectToolCommands.GetDirectRoute(RUNTIME_MUTATION_TOOL_NAME),
                new Dictionary<string, object>
                {
                    { "expectedProjectPath", "D:/UnityProjects/BattleIdle" },
                    { "expectedProjectName", "BattleIdle" },
                },
                out object rawResponse);

            Assert.That(handled, Is.True);
            var response = RequireDictionary(rawResponse);
            Assert.That(response["success"], Is.EqualTo(true));
            var result = RequireDictionary(response["result"]);
            CollectionAssert.IsEmpty((string[])result["receivedKeys"]);
        }

        [Test]
        public void AssetWorkspace_CreateCopyDependenciesAndTransactionRollback()
        {
            const string sourcePath = TEST_FOLDER + "/Source.txt";
            const string folderPath = TEST_FOLDER + "/Nested/Content";
            const string copiedPath = folderPath + "/Copied.txt";
            File.WriteAllText(GetAbsolutePath(sourcePath), "source");
            AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);

            var folder = RequireDictionary(MCPAssetWorkspaceCommands.EnsureFolder(
                new Dictionary<string, object> { { "path", folderPath } }));
            Assert.That(folder["success"], Is.EqualTo(true));
            Assert.That(AssetDatabase.IsValidFolder(folderPath), Is.True);

            var copy = RequireDictionary(MCPAssetWorkspaceCommands.Copy(new Dictionary<string, object>
            {
                { "sourcePath", sourcePath }, { "targetPath", copiedPath }
            }));
            Assert.That(copy["success"], Is.EqualTo(true));
            Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(copiedPath), Is.Not.Null);

            const string ussPath = TEST_FOLDER + "/Dependency.uss";
            const string uxmlPath = TEST_FOLDER + "/Dependent.uxml";
            File.WriteAllText(GetAbsolutePath(ussPath), ".dependency { color: red; }\n");
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);
            string ussGuid = AssetDatabase.AssetPathToGUID(ussPath);
            File.WriteAllText(GetAbsolutePath(uxmlPath),
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">" +
                $"<Style src=\"project://database/{ussPath.Replace(" ", "%20")}?fileID=7433441132597879392&amp;guid={ussGuid}&amp;type=3#Dependency\"/>" +
                "<ui:VisualElement class=\"dependency\"/></ui:UXML>\n");
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceSynchronousImport);

            var graph = RequireDictionary(MCPAssetWorkspaceCommands.Dependencies(
                new Dictionary<string, object>
                {
                    { "path", ussPath }, { "direction", "incoming" },
                    { "searchRoots", new[] { TEST_FOLDER } }
                }));
            var references = (List<Dictionary<string, object>>)graph["references"];
            Assert.That(references.Any(item => item["path"].ToString() == uxmlPath), Is.True);

            const string rolledBackPath = TEST_FOLDER + "/Rolled Back.txt";
            var transaction = RequireDictionary(MCPAssetWorkspaceCommands.Transaction(
                new Dictionary<string, object>
                {
                    { "operations", new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "copy" }, { "sourcePath", sourcePath },
                                { "targetPath", rolledBackPath }
                            }
                        }
                    },
                    { "requiredAssets", new[] { TEST_FOLDER + "/Missing.asset" } }
                }));
            Assert.That(transaction["success"], Is.EqualTo(false));
            Assert.That(transaction["rolledBack"], Is.EqualTo(true));
            Assert.That(AssetDatabase.LoadMainAssetAtPath(rolledBackPath), Is.Null);
        }

        [Test]
        public void UIToolkitAuthoring_EditsStructuredUxmlAndUssAndRollsBackTransactions()
        {
            const string uxmlPath = TEST_FOLDER + "/Authoring.uxml";
            const string ussPath = TEST_FOLDER + "/Authoring.uss";
            const string originalUxml =
                "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"><ui:VisualElement name=\"content\" /></ui:UXML>\n";
            File.WriteAllText(GetAbsolutePath(uxmlPath), originalUxml);
            File.WriteAllText(GetAbsolutePath(ussPath), ".content { color: red; }\n");
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceSynchronousImport);

            var uxml = RequireDictionary(MCPUIAuthoringCommands.EditUxml(new Dictionary<string, object>
            {
                { "assetPath", uxmlPath },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "add-element" }, { "parentName", "content" },
                            { "elementType", "Label" },
                            { "attributes", new Dictionary<string, object> { { "name", "title" } } }
                        },
                        new Dictionary<string, object>
                        {
                            { "type", "set-text" }, { "name", "title" }, { "text", "Ready" }
                        }
                    }
                }
            }));
            Assert.That(uxml["success"], Is.EqualTo(true));
            Assert.That(File.ReadAllText(GetAbsolutePath(uxmlPath)), Does.Contain("text=\"Ready\""));

            var uss = RequireDictionary(MCPUIAuthoringCommands.EditUss(new Dictionary<string, object>
            {
                { "assetPath", ussPath },
                { "operations", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "type", "set-declaration" }, { "selector", ".content" },
                            { "property", "background-color" }, { "value", "blue" }
                        }
                    }
                }
            }));
            Assert.That(uss["success"], Is.EqualTo(true));
            Assert.That(File.ReadAllText(GetAbsolutePath(ussPath)), Does.Contain("background-color: blue;"));

            string beforeTransaction = File.ReadAllText(GetAbsolutePath(uxmlPath));
            var rolledBack = RequireDictionary(MCPUIAuthoringCommands.AuthoringTransaction(
                new Dictionary<string, object>
                {
                    { "edits", new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                { "kind", "uxml" }, { "assetPath", uxmlPath },
                                { "operations", new List<object>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            { "type", "set-text" }, { "name", "title" }, { "text", "Changed" }
                                        }
                                    }
                                }
                            },
                            new Dictionary<string, object>
                            {
                                { "kind", "uss" }, { "assetPath", ussPath },
                                { "operations", new List<object>
                                    {
                                        new Dictionary<string, object>
                                        {
                                            { "type", "remove-selector" }, { "selector", ".missing" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }));
            Assert.That(rolledBack["success"], Is.EqualTo(false));
            Assert.That(rolledBack["rolledBack"], Is.EqualTo(true));
            Assert.That(File.ReadAllText(GetAbsolutePath(uxmlPath)), Is.EqualTo(beforeTransaction));
        }

        [Test]
        public void RequestQueue_IdempotencyCancellationAndReloadRecoveryAreMetadataDriven()
        {
            string agentId = "queue-regression-" + Guid.NewGuid().ToString("N");
            string requestKey = agentId + "|scene/hierarchy|stable";
            var first = MCPRequestQueue.SubmitPersistentRequest(agentId, "scene/hierarchy", "POST", "{}",
                requestKey, out bool firstReused);
            var second = MCPRequestQueue.SubmitPersistentRequest(agentId, "scene/hierarchy", "POST", "{}",
                requestKey, out bool secondReused);
            Assert.That(firstReused, Is.False);
            Assert.That(secondReused, Is.True);
            Assert.That(second.TicketId, Is.EqualTo(first.TicketId));

            var wrongOwner = MCPRequestQueue.CancelTicket(first.TicketId, agentId + "-other");
            Assert.That(wrongOwner["success"], Is.EqualTo(false));
            var canceled = MCPRequestQueue.CancelTicket(first.TicketId, agentId);
            Assert.That(canceled["success"], Is.EqualTo(true));
            Assert.That(canceled["status"], Is.EqualTo(MCPRequestQueue.RequestStatus.Canceled.ToString()));

            var restore = typeof(MCPRequestQueue).GetMethod("TryRestorePersistentRequest",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(restore, Is.Not.Null);
            Dictionary<string, object> Snapshot(string route, bool readOnly) => new Dictionary<string, object>
            {
                { "ticketId", DateTime.UtcNow.Ticks + (readOnly ? 1 : 2) },
                { "agentId", agentId }, { "actionName", route },
                { "status", MCPRequestQueue.RequestStatus.Executing.ToString() },
                { "submittedAt", DateTime.UtcNow.ToString("O") },
                { "persistentBody", "{}" }, { "persistentMethod", "POST" },
                { "isReadOnly", readOnly }
            };

            var readArguments = new object[] { Snapshot("scene/hierarchy", true), null };
            Assert.That(restore.Invoke(null, readArguments), Is.EqualTo(true));
            Assert.That(((MCPRequestQueue.RequestTicket)readArguments[1]).Status,
                Is.EqualTo(MCPRequestQueue.RequestStatus.Queued));

            var writeArguments = new object[] { Snapshot("asset/create-folder", false), null };
            Assert.That(restore.Invoke(null, writeArguments), Is.EqualTo(true));
            var uncertain = (MCPRequestQueue.RequestTicket)writeArguments[1];
            Assert.That(uncertain.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.UncertainAfterReload));
            Assert.That(uncertain.Retryable, Is.False);

            var refreshArguments = new object[] { Snapshot("asset/refresh", false), null };
            Assert.That(restore.Invoke(null, refreshArguments), Is.EqualTo(true));
            var refresh = (MCPRequestQueue.RequestTicket)refreshArguments[1];
            Assert.That(refresh.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.Queued));
            Assert.That(refresh.ResumeCount, Is.EqualTo(1));

            var playModeArguments = new object[] { Snapshot("editor/play-mode", false), null };
            Assert.That(restore.Invoke(null, playModeArguments), Is.EqualTo(true));
            var playMode = (MCPRequestQueue.RequestTicket)playModeArguments[1];
            Assert.That(playMode.Status, Is.EqualTo(MCPRequestQueue.RequestStatus.Queued));
            Assert.That(playMode.ResumeCount, Is.EqualTo(1));
        }

        [Test]
        public void PlayModeActionsResolveToExplicitIdempotentTargetStates()
        {
            MethodInfo method = typeof(MCPEditorCommands).GetMethod("TryResolvePlayModeTarget",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            object[] pause =
            {
                new Dictionary<string, object> { { "action", "pause" } },
                null, false, false, null
            };
            Assert.That(method.Invoke(null, pause), Is.EqualTo(true));
            Assert.That(pause[1], Is.EqualTo("pause"));
            Assert.That(pause[2], Is.EqualTo(true));
            Assert.That(pause[3], Is.EqualTo(true));

            object[] resume =
            {
                new Dictionary<string, object> { { "action", "resume" } },
                null, false, true, null
            };
            Assert.That(method.Invoke(null, resume), Is.EqualTo(true));
            Assert.That(resume[2], Is.EqualTo(true));
            Assert.That(resume[3], Is.EqualTo(false));

            object[] invalid =
            {
                new Dictionary<string, object> { { "action", "toggle" } },
                null, false, false, null
            };
            Assert.That(method.Invoke(null, invalid), Is.EqualTo(false));
            Assert.That(invalid[4], Does.Contain("'resume'"));
        }

        [Test]
        public void AssetRefresh_IdempotencyRequiresMatchingOwnerAndRequestId()
        {
            Type workflow = typeof(MCPToolMetadata).Assembly.GetType(
                "UnityMCP.Editor.MCPAssetRefreshWorkflow");
            Assert.That(workflow, Is.Not.Null);
            var method = workflow.GetMethod("IsSameRequest",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var existing = new Dictionary<string, object>
            {
                { "_agentId", "agent-a" }, { "_requestId", "request-1" }
            };

            Assert.That(method.Invoke(null, new object[]
            {
                existing, new Dictionary<string, object>
                {
                    { "_agentId", "agent-a" }, { "_requestId", "request-1" }
                }
            }), Is.EqualTo(true));
            Assert.That(method.Invoke(null, new object[]
            {
                existing, new Dictionary<string, object>
                {
                    { "_agentId", "agent-b" }, { "_requestId", "request-1" }
                }
            }), Is.EqualTo(false));
            Assert.That(method.Invoke(null, new object[]
            {
                existing, new Dictionary<string, object>
                {
                    { "_agentId", "agent-a" }, { "_requestId", "request-2" }
                }
            }), Is.EqualTo(false));

            var matchesRequestId = workflow.GetMethod("MatchesRequestId",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(matchesRequestId, Is.Not.Null);
            Assert.That(matchesRequestId.Invoke(null, new object[] { existing, "request-1" }),
                Is.EqualTo(true));
            Assert.That(matchesRequestId.Invoke(null, new object[] { existing, "request-2" }),
                Is.EqualTo(false));
        }

        [Test]
        public void AssetRefresh_ExactJobIdentityCanBePolledAcrossReloadOwnerChange()
        {
            Type workflow = typeof(MCPToolMetadata).Assembly.GetType(
                "UnityMCP.Editor.MCPAssetRefreshWorkflow");
            Assert.That(workflow, Is.Not.Null);
            FieldInfo jobField = workflow.GetField("_job", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo getMethod = workflow.GetMethod("Get", BindingFlags.Static | BindingFlags.Public);
            Assert.That(jobField, Is.Not.Null);
            Assert.That(getMethod, Is.Not.Null);

            object original = jobField.GetValue(null);
            object job = Activator.CreateInstance(jobField.FieldType, true);
            jobField.FieldType.GetField("JobId")?.SetValue(job, "refresh-job-1");
            jobField.FieldType.GetField("Status")?.SetValue(job, "succeeded");
            jobField.FieldType.GetField("Arguments")?.SetValue(job,
                new Dictionary<string, object>
                {
                    { "_agentId", "agent-before-reload" },
                    { "_requestId", "request-before-reload" },
                });
            jobField.FieldType.GetField("StartedAt")?.SetValue(job, DateTime.UtcNow);
            jobField.FieldType.GetField("UpdatedAt")?.SetValue(job, DateTime.UtcNow);

            try
            {
                jobField.SetValue(null, job);

                var recovered = RequireDictionary(getMethod.Invoke(null, new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "_agentId", "agent-after-reload" },
                        { "jobId", "refresh-job-1" },
                    }
                }));
                Assert.That(recovered["success"], Is.EqualTo(true));
                Assert.That(recovered["recoveredAcrossOwner"], Is.EqualTo(true));
                Assert.That(recovered["recoveryMatchedBy"], Is.EqualTo("jobId"));

                var implicitLookup = RequireDictionary(getMethod.Invoke(null, new object[]
                {
                    new Dictionary<string, object> { { "_agentId", "agent-after-reload" } }
                }));
                Assert.That(implicitLookup["errorCode"], Is.EqualTo("job_owner_mismatch"));

                var crossOwnerClear = RequireDictionary(getMethod.Invoke(null, new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "_agentId", "agent-after-reload" },
                        { "refreshRequestId", "request-before-reload" },
                        { "clear", true },
                    }
                }));
                Assert.That(crossOwnerClear["errorCode"], Is.EqualTo("job_owner_mismatch"));
            }
            finally
            {
                jobField.SetValue(null, original);
            }
        }

        [Test]
        public void AssetRefresh_PollSettlesIdleWaitingJobWithoutReconnect()
        {
            Type workflow = typeof(MCPToolMetadata).Assembly.GetType(
                "UnityMCP.Editor.MCPAssetRefreshWorkflow");
            Assert.That(workflow, Is.Not.Null);
            FieldInfo jobField = workflow.GetField("_job", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo getMethod = workflow.GetMethod("Get", BindingFlags.Static | BindingFlags.Public);
            MethodInfo getJobPath = workflow.GetMethod("GetJobPath",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo ensureUpdateRegistered = workflow.GetMethod("EnsureUpdateRegistered",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(jobField, Is.Not.Null);
            Assert.That(getMethod, Is.Not.Null);
            Assert.That(getJobPath, Is.Not.Null);
            Assert.That(ensureUpdateRegistered, Is.Not.Null);

            object original = jobField.GetValue(null);
            string jobPath = (string)getJobPath.Invoke(null, null);
            byte[] originalJobFile = File.Exists(jobPath) ? File.ReadAllBytes(jobPath) : null;
            object job = Activator.CreateInstance(jobField.FieldType, true);
            jobField.FieldType.GetField("JobId")?.SetValue(job, "refresh-job-idle");
            jobField.FieldType.GetField("Status")?.SetValue(job, "waiting-for-editor");
            jobField.FieldType.GetField("Arguments")?.SetValue(job,
                new Dictionary<string, object>
                {
                    { "_agentId", "refresh-agent" },
                    { "_requestId", "refresh-request" },
                    { "saveAssets", false },
                });
            jobField.FieldType.GetField("Result")?.SetValue(job,
                new Dictionary<string, object> { { "success", true } });
            jobField.FieldType.GetField("IdleSince")?.SetValue(job,
                DateTime.UtcNow - TimeSpan.FromSeconds(2));
            jobField.FieldType.GetField("StartedAt")?.SetValue(job, DateTime.UtcNow - TimeSpan.FromSeconds(3));
            jobField.FieldType.GetField("UpdatedAt")?.SetValue(job, DateTime.UtcNow - TimeSpan.FromSeconds(2));

            try
            {
                jobField.SetValue(null, job);
                var response = RequireDictionary(getMethod.Invoke(null, new object[]
                {
                    new Dictionary<string, object>
                    {
                        { "_agentId", "refresh-agent" },
                        { "jobId", "refresh-job-idle" },
                    }
                }));

                Assert.That(response["status"], Is.EqualTo("succeeded"));
                var result = RequireDictionary(response["result"]);
                Assert.That(result["settledAfterRefresh"], Is.EqualTo(true));
                Assert.That(result["isUpdating"], Is.EqualTo(false));
                Assert.That(result["isCompiling"], Is.EqualTo(false));
            }
            finally
            {
                jobField.SetValue(null, original);
                if (original != null)
                {
                    var isTerminal = jobField.FieldType.GetProperty("IsTerminal",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (isTerminal != null && Equals(isTerminal.GetValue(original), false))
                        ensureUpdateRegistered.Invoke(null, null);
                }
                if (originalJobFile == null)
                {
                    if (File.Exists(jobPath)) File.Delete(jobPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(jobPath));
                    File.WriteAllBytes(jobPath, originalJobFile);
                }
            }
        }

        [Test]
        public void MutatingRoutesRequireAnExplicitTargetProject()
        {
            var method = typeof(MCPBridgeServer).GetMethod("TryBuildProjectMismatchResponse",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var writeArguments = new object[]
            {
                "asset/create-folder", new Dictionary<string, object>(), null
            };
            Assert.That(method.Invoke(null, writeArguments), Is.EqualTo(true));
            Assert.That(RequireDictionary(writeArguments[2])["errorCode"],
                Is.EqualTo("target_project_required"));

            var readArguments = new object[]
            {
                "scene/hierarchy", new Dictionary<string, object>(), null
            };
            Assert.That(method.Invoke(null, readArguments), Is.EqualTo(false));
            Assert.That(readArguments[2], Is.Null);

            var copy = typeof(MCPBridgeServer).GetMethod("CopyArgumentIfMissing",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(copy, Is.Not.Null);
            var outer = new Dictionary<string, object>
            {
                { "expectedProjectPath", "D:/UnityProjects/MarbleBattlers" }
            };
            var inner = new Dictionary<string, object>();
            copy.Invoke(null, new object[] { outer, inner, "expectedProjectPath" });
            Assert.That(inner["expectedProjectPath"], Is.EqualTo(outer["expectedProjectPath"]));
        }

        [Test]
        public void JobHistoryIsPaginatedAndOwnerScoped()
        {
            Type history = typeof(MCPToolMetadata).Assembly.GetType("UnityMCP.Editor.MCPJobHistory");
            Assert.That(history, Is.Not.Null);
            MethodInfo record = history.GetMethod("Record", BindingFlags.Static | BindingFlags.Public);
            MethodInfo list = history.GetMethod("List", BindingFlags.Static | BindingFlags.Public);
            Assert.That(record, Is.Not.Null);
            Assert.That(list, Is.Not.Null);
            string jobId = "job-regression-" + Guid.NewGuid().ToString("N");
            record.Invoke(null, new object[]
            {
                "regression", jobId, "owner-a", "Completed",
                new Dictionary<string, object> { { "success", true } }
            });

            var ownerPage = RequireDictionary(list.Invoke(null, new object[]
            {
                new Dictionary<string, object>
                {
                    { "_agentId", "owner-a" }, { "jobType", "regression" }, { "limit", 1 }
                }
            }));
            Assert.That(Convert.ToInt32(ownerPage["total"]), Is.GreaterThanOrEqualTo(1));
            var otherPage = RequireDictionary(list.Invoke(null, new object[]
            {
                new Dictionary<string, object>
                {
                    { "_agentId", "owner-b" }, { "jobType", "regression" }
                }
            }));
            Assert.That(Convert.ToInt32(otherPage["total"]), Is.EqualTo(0));
        }

        [Test]
        public void RouteRegistryMatchesEveryBuiltInSwitchHandlerWithoutRuntimeSourceParsing()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBridgeServer).Assembly);
            Assert.That(package, Is.Not.Null);
            string source = File.ReadAllText(Path.Combine(package.resolvedPath, "Editor", "MCPBridgeServer.cs"));
            int methodIndex = source.LastIndexOf("private static object RouteRequest(string path",
                StringComparison.Ordinal);
            int switchIndex = source.IndexOf("switch (path)", methodIndex, StringComparison.Ordinal);
            int defaultIndex = source.IndexOf("\n                default:", switchIndex,
                StringComparison.Ordinal);
            Assert.That(methodIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(switchIndex, Is.GreaterThan(methodIndex));
            Assert.That(defaultIndex, Is.GreaterThan(switchIndex));
            var switchRoutes = Regex.Matches(source.Substring(switchIndex, defaultIndex - switchIndex),
                    "case\\s+\"([^\"]+)\"\\s*:")
                .Cast<Match>().Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

            Type registry = typeof(MCPToolMetadata).Assembly.GetType("UnityMCP.Editor.MCPRouteRegistry");
            Assert.That(registry, Is.Not.Null);
            var property = registry.GetProperty("BuiltInRoutes", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            var registered = ((IEnumerable<string>)property.GetValue(null)).ToHashSet(StringComparer.Ordinal);
            CollectionAssert.IsSubsetOf(switchRoutes, registered);
            CollectionAssert.AreEquivalent(new[]
            {
                "_meta/routes", "_meta/tools", "_meta/capabilities",
                "queue/cancel", "queue/info", "queue/status"
            }, registered.Except(switchRoutes).ToArray());
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

        [UnityTest]
        public IEnumerator AssetImportUnityPackage_PreservesGuidAndConfirmsCompletion()
        {
            const string assetPath = TEST_FOLDER + "/Unity Package Payload.txt";
            string packagePath = Path.Combine(Path.GetTempPath(),
                $"unity-mcp-import-3.3.10-{Guid.NewGuid():N}.unitypackage");
            try
            {
                File.WriteAllText(GetAbsolutePath(assetPath), "unitypackage payload");
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                string originalGuid = AssetDatabase.AssetPathToGUID(assetPath);
                Assert.That(originalGuid, Is.Not.Empty);

                AssetDatabase.ExportPackage(assetPath, packagePath, ExportPackageOptions.Default);
                Assert.That(File.Exists(packagePath), Is.True);
                Assert.That(AssetDatabase.DeleteAsset(assetPath), Is.True);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Null);

                var started = RequireDictionary(MCPAssetCommands.ImportUnityPackage(
                    new Dictionary<string, object> { { "packagePath", packagePath } }));

                Assert.That(started["success"], Is.EqualTo(true), MiniJson.Serialize(started));
                Assert.That(started["jobType"], Is.EqualTo("unitypackage-import"));
                Assert.That(started["pollRoute"], Is.EqualTo("jobs/get"));

                var result = started;
                string jobId = started["jobId"].ToString();
                double timeoutAt = EditorApplication.timeSinceStartup + 10d;
                while (result["status"].ToString() != "succeeded" &&
                       result["status"].ToString() != "failed" &&
                       result["status"].ToString() != "cancelled" &&
                       EditorApplication.timeSinceStartup < timeoutAt)
                {
                    yield return null;
                    result = RequireDictionary(MCPUnityPackageImportWorkflow.Get(
                        new Dictionary<string, object> { { "jobId", jobId } }));
                }

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["status"], Is.EqualTo("succeeded"));
                Assert.That(result["interactive"], Is.EqualTo(false));
                Assert.That(result["started"], Is.EqualTo(true));
                Assert.That(result["completed"], Is.EqualTo(true));
                Assert.That(result["cancelled"], Is.EqualTo(false));
                Assert.That(result["completionConfirmedBy"],
                    Is.EqualTo("AssetDatabase.importPackageCompleted"));
                Assert.That(AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath), Is.Not.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(originalGuid));
                Assert.That((List<string>)result["newAssetPaths"], Does.Contain(assetPath));
            }
            finally
            {
                if (File.Exists(packagePath))
                    File.Delete(packagePath);
            }
        }

        [Test]
        public void AssetImportUnityPackage_MissingFileReturnsStableFailure()
        {
            string packagePath = Path.Combine(Path.GetTempPath(),
                $"missing-unity-mcp-{Guid.NewGuid():N}.unitypackage");

            var result = RequireDictionary(MCPAssetCommands.ImportUnityPackage(
                new Dictionary<string, object> { { "packagePath", packagePath } }));

            Assert.That(result["success"], Is.EqualTo(false));
            Assert.That(result["status"], Is.EqualTo("failed"));
            Assert.That(result["errorCode"], Is.EqualTo("package_not_found"));
            Assert.That(result["packagePath"], Is.EqualTo(Path.GetFullPath(packagePath)));
            Assert.That(result["interactive"], Is.EqualTo(false));
        }

        [Test]
        public void AssetImport_ConfiguresTextureImporterInOneOperation()
        {
            string externalPath = CreateExternalPng(Color.white);
            const string spritePath = TEST_FOLDER + "/Imported Sprite.png";

            try
            {
                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object>
                        {
                            { "dedupeMode", "none" },
                            { "textureType", "Sprite" }, { "spriteMode", "Single" },
                            { "pixelsPerUnit", 8f }, { "filterMode", "Point" }, { "isReadable", true },
                            { "compression", "uncompressed" }, { "alphaIsTransparency", true },
                            { "meshType", "FullRect" }, { "mipmapEnabled", false },
                        }
                    },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", externalPath }, { "destinationPath", spritePath },
                            },
                        }
                    },
                    { "execution", new Dictionary<string, object> { { "mode", "immediate" } } },
                }));

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["importCount"], Is.EqualTo(1));
                Assert.That(result["importedCount"], Is.EqualTo(1));
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

        [UnityTest]
        public IEnumerator AssetImportDeferred_BatchedModeImportsAllAndAllowsItemOverrides()
        {
            string firstSource = CreateExternalPng(Color.red);
            string secondSource = CreateExternalPng(Color.blue);
            const string firstPath = TEST_FOLDER + "/Batch First.png";
            const string secondPath = TEST_FOLDER + "/Batch Second.png";
            object completed = null;
            try
            {
                MCPAssetCommands.ImportDeferred(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object>
                        {
                            { "dedupeMode", "none" },
                            { "textureType", "Sprite" }, { "spriteMode", "Single" },
                            { "pixelsPerUnit", 12f }, { "filterMode", "Point" },
                            { "compression", "uncompressed" },
                        }
                    },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", firstSource }, { "destinationPath", firstPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", secondSource }, { "destinationPath", secondPath },
                                { "pixelsPerUnit", 24f },
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

                Assert.That(completed, Is.Not.Null);
                var result = RequireDictionary(completed);
                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["importedCount"], Is.EqualTo(2));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(firstPath)).spritePixelsPerUnit,
                    Is.EqualTo(12f));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(secondPath)).spritePixelsPerUnit,
                    Is.EqualTo(24f));
            }
            finally
            {
                if (File.Exists(firstSource)) File.Delete(firstSource);
                if (File.Exists(secondSource)) File.Delete(secondSource);
            }
        }

        [Test]
        public void AssetImport_InvalidBatchFailsPreflightBeforeCreatingAnyAsset()
        {
            string validSource = CreateExternalPng(Color.green);
            const string firstPath = TEST_FOLDER + "/Preflight First.png";
            const string secondPath = TEST_FOLDER + "/Preflight Second.png";
            try
            {
                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object> { { "dedupeMode", "none" } } },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", validSource }, { "destinationPath", firstPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", Path.Combine(Path.GetTempPath(), $"missing-unity-mcp-{Guid.NewGuid():N}.png") },
                                { "destinationPath", secondPath },
                            },
                        }
                    },
                }));

                Assert.That(result["success"], Is.EqualTo(false));
                Assert.That(result["error"].ToString(), Does.Contain("Import 1 is invalid"));
                Assert.That(File.Exists(GetAbsolutePath(firstPath)), Is.False);
                Assert.That(File.Exists(GetAbsolutePath(secondPath)), Is.False);
            }
            finally
            {
                if (File.Exists(validSource)) File.Delete(validSource);
            }
        }

        [UnityTest]
        public IEnumerator AssetImportDeferred_FailureRollsBackEarlierImports()
        {
            string firstSource = CreateExternalPng(Color.yellow);
            string secondSource = CreateExternalPng(Color.magenta);
            const string firstPath = TEST_FOLDER + "/Rollback First.png";
            const string secondPath = TEST_FOLDER + "/Rollback Second.png";
            object completed = null;
            try
            {
                MCPAssetCommands.ImportDeferred(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object> { { "dedupeMode", "none" } } },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", firstSource }, { "destinationPath", firstPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", secondSource }, { "destinationPath", secondPath },
                            },
                        }
                    },
                    { "execution", new Dictionary<string, object>
                        {
                            { "mode", "batched" }, { "operationsPerFrame", 1 }, { "frameBudgetMs", 1 },
                        }
                    },
                }, result => completed = result, _ => { });

                Assert.That(File.Exists(GetAbsolutePath(firstPath)), Is.True,
                    "The first frame should complete one import before the injected failure.");
                File.Delete(secondSource);
                double timeoutAt = EditorApplication.timeSinceStartup + 10d;
                while (completed == null && EditorApplication.timeSinceStartup < timeoutAt)
                    yield return null;

                Assert.That(completed, Is.Not.Null);
                var result = RequireDictionary(completed);
                Assert.That(result["success"], Is.EqualTo(false));
                Assert.That(result["allTouchedRolledBack"], Is.EqualTo(true));
                Assert.That(result["rolledBackCount"], Is.EqualTo(1));
                Assert.That(File.Exists(GetAbsolutePath(firstPath)), Is.False);
                Assert.That(File.Exists(GetAbsolutePath(secondPath)), Is.False);
            }
            finally
            {
                if (File.Exists(firstSource)) File.Delete(firstSource);
                if (File.Exists(secondSource)) File.Delete(secondSource);
            }
        }

        [UnityTest]
        public IEnumerator AssetImportDeferred_RollbackRestoresOverwrittenAssetAndImporter()
        {
            string originalSource = CreateExternalPng(Color.black);
            string replacementSource = CreateExternalPng(Color.white);
            string failingSource = CreateExternalPng(Color.cyan);
            const string existingPath = TEST_FOLDER + "/Existing Sprite.png";
            const string failingPath = TEST_FOLDER + "/Failure Trigger.png";
            object completed = null;
            try
            {
                File.Copy(originalSource, GetAbsolutePath(existingPath), true);
                AssetDatabase.ImportAsset(existingPath, ImportAssetOptions.ForceUpdate);
                var originalImporter = (TextureImporter)AssetImporter.GetAtPath(existingPath);
                originalImporter.textureType = TextureImporterType.Sprite;
                originalImporter.filterMode = FilterMode.Bilinear;
                originalImporter.SaveAndReimport();
                byte[] originalBytes = File.ReadAllBytes(GetAbsolutePath(existingPath));
                string originalGuid = AssetDatabase.AssetPathToGUID(existingPath);

                MCPAssetCommands.ImportDeferred(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object>
                        {
                            { "dedupeMode", "none" }, { "overwrite", true },
                            { "textureType", "Sprite" }, { "filterMode", "Point" },
                        }
                    },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", replacementSource }, { "destinationPath", existingPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", failingSource }, { "destinationPath", failingPath },
                            },
                        }
                    },
                    { "execution", new Dictionary<string, object>
                        {
                            { "mode", "batched" }, { "operationsPerFrame", 1 }, { "frameBudgetMs", 1 },
                        }
                    },
                }, result => completed = result, _ => { });

                Assert.That(((TextureImporter)AssetImporter.GetAtPath(existingPath)).filterMode,
                    Is.EqualTo(FilterMode.Point));
                File.Delete(failingSource);
                double timeoutAt = EditorApplication.timeSinceStartup + 10d;
                while (completed == null && EditorApplication.timeSinceStartup < timeoutAt)
                    yield return null;

                Assert.That(completed, Is.Not.Null);
                var result = RequireDictionary(completed);
                Assert.That(result["success"], Is.EqualTo(false));
                Assert.That(result["allTouchedRolledBack"], Is.EqualTo(true));
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(GetAbsolutePath(existingPath)));
                Assert.That(AssetDatabase.AssetPathToGUID(existingPath), Is.EqualTo(originalGuid));
                Assert.That(((TextureImporter)AssetImporter.GetAtPath(existingPath)).filterMode,
                    Is.EqualTo(FilterMode.Bilinear));
                Assert.That(File.Exists(GetAbsolutePath(failingPath)), Is.False);
            }
            finally
            {
                if (File.Exists(originalSource)) File.Delete(originalSource);
                if (File.Exists(replacementSource)) File.Delete(replacementSource);
                if (File.Exists(failingSource)) File.Delete(failingSource);
            }
        }

        [Test]
        public void AssetImport_DefaultPixelDedupeSkipsExistingImageWithDifferentFileBytes()
        {
            string sourcePath = CreateExternalPng(new Color(0.13f, 0.37f, 0.73f, 1f));
            const string existingPath = TEST_FOLDER + "/Existing Pixel Match.png";
            const string destinationPath = TEST_FOLDER + "/Skipped Pixel Match.png";
            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourcePath);
                File.WriteAllBytes(GetAbsolutePath(existingPath), sourceBytes.Concat(new byte[] { 1, 2, 3, 4 }).ToArray());
                AssetDatabase.ImportAsset(existingPath, ImportAssetOptions.ForceSynchronousImport);

                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", sourcePath }, { "destinationPath", destinationPath },
                            },
                        }
                    },
                }));

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["importedCount"], Is.EqualTo(0));
                Assert.That(result["skippedCount"], Is.EqualTo(1));
                Assert.That(result["duplicateCount"], Is.EqualTo(1));
                var import = ((List<Dictionary<string, object>>)result["imports"])[0];
                Assert.That(import["dedupeMode"], Is.EqualTo("decodedPixels"));
                Assert.That(import["skipped"], Is.EqualTo(true));
                Assert.That(import["duplicateAssetPath"], Is.EqualTo(existingPath));
                Assert.That(import["duplicateAssetGuid"].ToString(), Is.Not.Empty);
                Assert.That(File.Exists(GetAbsolutePath(destinationPath)), Is.False);
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
            }
        }

        [Test]
        public void AssetImport_DedupeSkipsLaterDuplicateInsideBatch()
        {
            string sourcePath = CreateExternalPng(new Color(0.81f, 0.42f, 0.17f, 1f));
            const string firstPath = TEST_FOLDER + "/Unique Batch Image.png";
            const string secondPath = TEST_FOLDER + "/Duplicate Batch Image.png";
            try
            {
                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object>
                        {
                            { "dedupeMode", "decodedPixels" }, { "dedupeScope", "destinationFolder" },
                        }
                    },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", sourcePath }, { "destinationPath", firstPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", sourcePath }, { "destinationPath", secondPath },
                            },
                        }
                    },
                }));

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["importedCount"], Is.EqualTo(1));
                Assert.That(result["skippedCount"], Is.EqualTo(1));
                var imports = (List<Dictionary<string, object>>)result["imports"];
                Assert.That(imports[0]["imported"], Is.EqualTo(true));
                Assert.That(imports[1]["skipped"], Is.EqualTo(true));
                Assert.That(imports[1]["duplicateSourceIndex"], Is.EqualTo(0));
                Assert.That(File.Exists(GetAbsolutePath(firstPath)), Is.True);
                Assert.That(File.Exists(GetAbsolutePath(secondPath)), Is.False);
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
            }
        }

        [Test]
        public void AssetImport_DuplicateErrorFailsWholeBatchDuringPreflight()
        {
            string sourcePath = CreateExternalPng(new Color(0.56f, 0.21f, 0.64f, 1f));
            const string firstPath = TEST_FOLDER + "/Error First.png";
            const string secondPath = TEST_FOLDER + "/Error Second.png";
            try
            {
                var result = RequireDictionary(MCPAssetCommands.Import(new Dictionary<string, object>
                {
                    { "defaults", new Dictionary<string, object>
                        {
                            { "dedupeMode", "decodedPixels" }, { "dedupeScope", "destinationFolder" },
                            { "onDuplicate", "error" },
                        }
                    },
                    { "imports", new object[]
                        {
                            new Dictionary<string, object>
                            {
                                { "sourcePath", sourcePath }, { "destinationPath", firstPath },
                            },
                            new Dictionary<string, object>
                            {
                                { "sourcePath", sourcePath }, { "destinationPath", secondPath },
                            },
                        }
                    },
                }));

                Assert.That(result["success"], Is.EqualTo(false));
                Assert.That(result["error"].ToString(), Does.Contain("Import 1 is invalid"));
                Assert.That(result["error"].ToString(), Does.Contain("duplicates import 0"));
                Assert.That(File.Exists(GetAbsolutePath(firstPath)), Is.False);
                Assert.That(File.Exists(GetAbsolutePath(secondPath)), Is.False);
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
            }
        }

        [Test]
        public void TextureFindDuplicates_FindsDecodedPixelMatchesWithDifferentFileBytes()
        {
            string sourcePath = CreateExternalPng(new Color(0.24f, 0.68f, 0.33f, 1f));
            const string firstPath = TEST_FOLDER + "/Audit First.png";
            const string secondPath = TEST_FOLDER + "/Audit Second.png";
            try
            {
                byte[] sourceBytes = File.ReadAllBytes(sourcePath);
                File.WriteAllBytes(GetAbsolutePath(firstPath), sourceBytes);
                File.WriteAllBytes(GetAbsolutePath(secondPath), sourceBytes.Concat(new byte[] { 9, 8, 7 }).ToArray());
                AssetDatabase.ImportAsset(firstPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(secondPath, ImportAssetOptions.ForceSynchronousImport);

                var result = RequireDictionary(MCPImageDuplicateCommands.FindDuplicates(
                    new Dictionary<string, object>
                    {
                        { "folder", TEST_FOLDER }, { "mode", "decodedPixels" },
                    }));

                Assert.That(result["success"], Is.EqualTo(true));
                Assert.That(result["duplicateGroupCount"], Is.EqualTo(1));
                var groups = (List<Dictionary<string, object>>)result["groups"];
                var assets = (List<Dictionary<string, object>>)groups[0]["assets"];
                Assert.That(assets.Select(asset => asset["path"].ToString()),
                    Is.EquivalentTo(new[] { firstPath, secondPath }));
            }
            finally
            {
                if (File.Exists(sourcePath)) File.Delete(sourcePath);
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
        public void PrefabSetProperty_UnwrapsScalarValueEnvelopeForString()
        {
            CreateTestPrefab();
            AddComponentToTestPrefab<TextMesh>();

            var result = RequireDictionary(MCPPrefabAssetCommands.SetComponentProperty(
                new Dictionary<string, object>
                {
                    { "assetPath", PREFAB_PATH },
                    { "prefabPath", "Source" },
                    { "componentType", typeof(TextMesh).FullName },
                    { "propertyName", "m_Text" },
                    { "value", new Dictionary<string, object> { { "value", "Wrapped Text" } } },
                }));

            Assert.That(result["success"], Is.EqualTo(true));
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                Assert.That(root.transform.Find("Source").GetComponent<TextMesh>().text, Is.EqualTo("Wrapped Text"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [Test]
        public void SetSerializedValue_UnwrapsScalarValueEnvelopeForNestedEnum()
        {
            var target = ScriptableObject.CreateInstance<ScalarEnvelopeTestObject>();
            try
            {
                var serialized = new SerializedObject(target);
                var property = serialized.FindProperty("config.mode");
                Assert.That(property, Is.Not.Null);
                Assert.That(property.propertyType, Is.EqualTo(SerializedPropertyType.Enum));

                var setSerializedValue = typeof(MCPComponentCommands).GetMethod("SetSerializedValue",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(setSerializedValue, Is.Not.Null);
                setSerializedValue.Invoke(null, new object[]
                {
                    property,
                    new Dictionary<string, object> { { "value", nameof(ScalarEnvelopeTestMode.Second) } }
                });
                serialized.ApplyModifiedProperties();

                Assert.That(target.config.mode, Is.EqualTo(ScalarEnvelopeTestMode.Second));
            }
            finally
            {
                Object.DestroyImmediate(target);
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
        public void ToolMetadata_ExposesPrefabConfigureComponentAsFirstClass()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: true, includeSchema: true, limit: 200));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            var tool = tools.Single(item =>
                item["route"].ToString() == "prefab-asset/configure-component");

            Assert.That(tool["toolName"], Is.EqualTo("unity_prefab_asset_configure_component"));
            Assert.That(tool["firstClass"], Is.EqualTo(true));
            var schema = RequireDictionary(tool["inputSchema"]);
            CollectionAssert.AreEquivalent(new[] { "assetPath", "componentType" },
                (List<string>)schema["required"]);
            var properties = RequireDictionary(schema["properties"]);
            Assert.That(properties.Keys, Does.Contain("properties"));
            Assert.That(properties.Keys, Does.Contain("references"));
            Assert.That(properties.Keys, Does.Contain("expectedProjectPath"));
        }

        [Test]
        public void ToolMetadata_ExposesImageImportAndTextureImporterToolsAsFirstClass()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: true, includeSchema: true, limit: 200));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            var toolsByRoute = tools.ToDictionary(tool => tool["route"].ToString());

            foreach (var expected in new[]
                     {
                         (Route: "asset/import", ToolName: "unity_asset_import"),
                         (Route: "texture/apply-sprite-preset", ToolName: "unity_texture_apply_sprite_preset"),
                         (Route: "texture/info", ToolName: "unity_texture_info"),
                         (Route: "texture/find-duplicates", ToolName: "unity_texture_find_duplicates"),
                     })
            {
                Assert.That(toolsByRoute.ContainsKey(expected.Route), Is.True,
                    $"{expected.Route} must be exposed as a first-class MCP tool.");
                var tool = toolsByRoute[expected.Route];
                Assert.That(tool["toolName"], Is.EqualTo(expected.ToolName));
                Assert.That(tool["firstClass"], Is.EqualTo(true));

                var schema = RequireDictionary(tool["inputSchema"]);
                var properties = RequireDictionary(schema["properties"]);
                Assert.That(properties, Is.Not.Empty, $"{expected.Route} must publish a concrete input schema.");
            }

            var textureInfoAnnotations = RequireDictionary(toolsByRoute["texture/info"]["annotations"]);
            Assert.That(textureInfoAnnotations["readOnlyHint"], Is.EqualTo(true));
            var duplicateFinderAnnotations = RequireDictionary(
                toolsByRoute["texture/find-duplicates"]["annotations"]);
            Assert.That(duplicateFinderAnnotations["readOnlyHint"], Is.EqualTo(true));

            var assetImportSchema = RequireDictionary(toolsByRoute["asset/import"]["inputSchema"]);
            var assetImportProperties = RequireDictionary(assetImportSchema["properties"]);
            Assert.That(assetImportProperties.Keys,
                Is.EquivalentTo(new[]
                {
                    "dryRun", "defaults", "execution", "imports", "expectedProjectPath"
                }));
            Assert.That(assetImportProperties.ContainsKey("sourcePath"), Is.False);
            Assert.That(assetImportProperties.ContainsKey("destinationPath"), Is.False);
            var defaultsSchema = RequireDictionary(assetImportProperties["defaults"]);
            var defaultProperties = RequireDictionary(defaultsSchema["properties"]);
            Assert.That(defaultProperties.Keys, Does.Contain("dedupeMode"));
            Assert.That(defaultProperties.Keys, Does.Contain("dedupeScope"));
            Assert.That(defaultProperties.Keys, Does.Contain("dedupeSearchPath"));
            Assert.That(defaultProperties.Keys, Does.Contain("onDuplicate"));
        }

        [Test]
        public void ToolMetadata_ExposesUnityPackageImportAsFirstClass()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: false, includeSchema: true, limit: 300));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            var tool = tools.Single(item => item["route"].ToString() == "asset/import-unitypackage");

            Assert.That(tool["toolName"], Is.EqualTo("unity_asset_import_unitypackage"));
            Assert.That(tool["firstClass"], Is.EqualTo(true));
            Assert.That(tool["mutatesAssets"], Is.EqualTo(true));
            Assert.That(tool["longRunning"], Is.EqualTo(true));
            Assert.That(tool["mayReloadDomain"], Is.EqualTo(true));

            var schema = RequireDictionary(tool["inputSchema"]);
            CollectionAssert.AreEquivalent(new[] { "packagePath" }, (List<string>)schema["required"]);
            var properties = RequireDictionary(schema["properties"]);
            Assert.That(properties.Keys, Does.Contain("packagePath"));
            Assert.That(properties.Keys, Does.Contain("expectedProjectPath"));
            Assert.That(properties.Keys, Does.Not.Contain("interactive"));
        }

        [Test]
        public void ToolMetadata_ExposesDocumentedAnimatorEditingToolsAsFirstClass()
        {
            var result = RequireDictionary(MCPToolMetadata.GetRegisteredTools(
                firstClassOnly: true, compact: true, includeSchema: true, limit: 200));
            var tools = (List<Dictionary<string, object>>)result["tools"];
            var toolsByRoute = tools.ToDictionary(tool => tool["route"].ToString());

            foreach (var expected in new[]
                     {
                         (Route: "animation/transition-info", ToolName: "unity_animation_transition_info"),
                         (Route: "animation/update-state", ToolName: "unity_animation_update_state"),
                         (Route: "animation/update-transition", ToolName: "unity_animation_update_transition"),
                         (Route: "animation/connect-states", ToolName: "unity_animation_connect_states"),
                     })
            {
                Assert.That(toolsByRoute.ContainsKey(expected.Route), Is.True,
                    $"{expected.Route} must be exposed as a first-class MCP tool.");
                var tool = toolsByRoute[expected.Route];
                Assert.That(tool["toolName"], Is.EqualTo(expected.ToolName));
                Assert.That(tool["firstClass"], Is.EqualTo(true));

                var schema = RequireDictionary(tool["inputSchema"]);
                var properties = RequireDictionary(schema["properties"]);
                Assert.That(properties, Is.Not.Empty, $"{expected.Route} must publish a concrete input schema.");
            }

            var transitionInfoAnnotations = RequireDictionary(
                toolsByRoute["animation/transition-info"]["annotations"]);
            Assert.That(transitionInfoAnnotations["readOnlyHint"], Is.EqualTo(true));

            foreach (string route in new[]
                     {
                         "animation/update-state",
                         "animation/update-transition",
                         "animation/connect-states",
                     })
            {
                var annotations = RequireDictionary(toolsByRoute[route]["annotations"]);
                Assert.That(annotations.ContainsKey("readOnlyHint"), Is.False);
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
            Type registry = typeof(MCPToolMetadata).Assembly.GetType("UnityMCP.Editor.MCPCapabilityRegistry");
            Assert.That(registry, Is.Not.Null);
            var method = registry.GetMethod("IsRouteAvailable", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Type localizationBridge = typeof(MCPToolMetadata).Assembly.GetType(
                "UnityMCP.Editor.MCPLocalizationBridge");
            Assert.That(localizationBridge, Is.Not.Null);
            var isAvailable = localizationBridge.GetProperty("IsAvailable",
                BindingFlags.Static | BindingFlags.Public);
            Assert.That(isAvailable, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { "localization/status" }),
                Is.EqualTo(isAvailable.GetValue(null)));
            Assert.That(method.Invoke(null, new object[] { "scene/hierarchy" }), Is.EqualTo(true));
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

        private static void AddComponentToTestPrefab<T>() where T : Component
        {
            var root = PrefabUtility.LoadPrefabContents(PREFAB_PATH);
            try
            {
                root.transform.Find("Source").gameObject.AddComponent<T>();
                Assert.That(PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH), Is.Not.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Dictionary<string, object> RequireDictionary(object value)
        {
            Assert.That(value, Is.TypeOf<Dictionary<string, object>>());
            return (Dictionary<string, object>)value;
        }

        private static Dictionary<string, object> InvokeUIBuilderPixelAnalysis(Color32[] pixels, int width,
            int height, RectInt documentRect, RectInt canvasRect)
        {
            MethodInfo analyzer = typeof(MCPUICommands).GetMethod("AnalyzeUIBuilderPixels",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(analyzer, Is.Not.Null);
            return RequireDictionary(analyzer.Invoke(null,
                new object[] { pixels, width, height, documentRect, canvasRect }));
        }

        private static Color32[] CreateTopLeftCheckerboard(int width, int height, RectInt rect,
            Color32 first, Color32 second)
        {
            var pixels = Enumerable.Repeat(new Color32(26, 26, 26, 255), width * height).ToArray();
            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                for (int x = rect.xMin; x < rect.xMax; x++)
                {
                    Color32 color = ((x / 8 + y / 8) & 1) == 0 ? first : second;
                    SetTopLeftPixel(pixels, width, height, x, y, color);
                }
            }

            return pixels;
        }

        private static void FillTopLeftRect(Color32[] pixels, int width, int height, RectInt rect, Color32 color)
        {
            for (int y = rect.yMin; y < rect.yMax; y++)
            {
                for (int x = rect.xMin; x < rect.xMax; x++)
                    SetTopLeftPixel(pixels, width, height, x, y, color);
            }
        }

        private static void SetTopLeftPixel(Color32[] pixels, int width, int height, int x, int y, Color32 color)
        {
            pixels[(height - 1 - y) * width + x] = color;
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

        private static string CreateExternalPng(Color color)
        {
            string path = Path.Combine(Path.GetTempPath(), $"unity-mcp-{Guid.NewGuid():N}.png");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(new[] { color, color, color, color });
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                return path;
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }
    }
}
