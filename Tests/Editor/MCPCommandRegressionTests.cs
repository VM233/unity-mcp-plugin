using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
