using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    public static class MCPSceneCommands
    {
        public static object GetSceneInfo()
        {
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var rootObjects = new List<string>();
                foreach (var go in scene.GetRootGameObjects())
                    rootObjects.Add(go.name);

                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "isDirty", scene.isDirty },
                    { "isLoaded", scene.isLoaded },
                    { "rootObjectCount", scene.rootCount },
                    { "rootObjects", rootObjects },
                    { "buildIndex", scene.buildIndex },
                });
            }

            return new Dictionary<string, object>
            {
                { "activeScene", SceneManager.GetActiveScene().name },
                { "sceneCount", SceneManager.sceneCount },
                { "scenes", scenes },
            };
        }

        public static object OpenScene(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            // Check for unsaved changes
            if (SceneManager.GetActiveScene().isDirty)
            {
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    var scene = EditorSceneManager.OpenScene(path);
                    return new { success = true, name = scene.name, path = scene.path };
                }
                return new { error = "Scene has unsaved changes and user cancelled" };
            }

            var openedScene = EditorSceneManager.OpenScene(path);
            return new { success = true, name = openedScene.name, path = openedScene.path };
        }

        public static object SaveScene()
        {
            var scene = SceneManager.GetActiveScene();
            bool saved = EditorSceneManager.SaveScene(scene);
            return new { success = saved, scene = scene.name, path = scene.path };
        }

        public static object NewScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            return new { success = true, name = scene.name };
        }

        public static object GetHierarchy(Dictionary<string, object> args)
        {
            int maxDepth = 10;
            if (args != null && args.ContainsKey("maxDepth"))
                maxDepth = System.Convert.ToInt32(args["maxDepth"]);
            maxDepth = Math.Max(0, Math.Min(maxDepth, 50));

            // Keep the default response small; callers can explore subtrees with parentPath.
            int maxNodes = 250;
            if (args != null && args.ContainsKey("maxNodes"))
                maxNodes = System.Convert.ToInt32(args["maxNodes"]);
            maxNodes = Math.Max(1, Math.Min(maxNodes, 2000));

            // Optional: only return hierarchy under a specific parent path
            string parentPath = null;
            if (args != null && args.ContainsKey("parentPath"))
                parentPath = args["parentPath"]?.ToString();

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            // Count total objects in scene for metadata
            int totalSceneObjects = CountAllObjects(rootObjects);

            // If parentPath specified, find that object and only return its subtree
            GameObject[] startObjects = rootObjects;
            if (!string.IsNullOrEmpty(parentPath))
            {
                var found = GameObject.Find(parentPath);
                if (found == null)
                    return new Dictionary<string, object>
                    {
                        { "error", $"GameObject not found at path: {parentPath}" },
                    };
                startObjects = new[] { found };
            }

            string componentTypeName = args != null && args.TryGetValue("componentType", out var componentTypeValue)
                ? componentTypeValue?.ToString()
                : null;
            if (!string.IsNullOrEmpty(componentTypeName))
            {
                Type componentType = MCPComponentCommands.FindType(componentTypeName);
                if (componentType == null || !typeof(Component).IsAssignableFrom(componentType))
                    return new Dictionary<string, object> { { "error", $"Component type not found: {componentTypeName}" } };

                int maxResults = args.ContainsKey("maxResults")
                    ? Math.Max(1, Math.Min(Convert.ToInt32(args["maxResults"]), 200))
                    : Math.Max(1, Math.Min(maxNodes, 50));
                int offset = args.ContainsKey("offset")
                    ? Math.Max(0, Convert.ToInt32(args["offset"]))
                    : 0;
                string nameContains = args.TryGetValue("nameContains", out var nameValue)
                    ? nameValue?.ToString()
                    : null;
                string pathContains = args.TryGetValue("pathContains", out var pathValue)
                    ? pathValue?.ToString()
                    : null;

                var matches = new List<object>();
                int totalMatches = 0;
                foreach (var root in startObjects)
                {
                    CollectComponentMatches(root, componentType, nameContains, pathContains, offset, maxResults,
                        matches, ref totalMatches);
                }

                int nextOffset = offset + matches.Count;

                var filteredResult = new Dictionary<string, object>
                {
                    { "scene", scene.name },
                    { "filtered", true },
                    { "componentType", componentType.FullName },
                    { "matches", matches },
                    { "matchCount", matches.Count },
                    { "totalMatches", totalMatches },
                    { "offset", offset },
                    { "maxResults", maxResults },
                    { "truncated", nextOffset < totalMatches },
                    { "hasMore", nextOffset < totalMatches },
                    { "nextOffset", nextOffset < totalMatches ? (object)nextOffset : null },
                    { "totalSceneObjects", totalSceneObjects },
                };
                if (!string.IsNullOrEmpty(parentPath))
                    filteredResult["parentPath"] = parentPath;
                if (!string.IsNullOrEmpty(nameContains))
                    filteredResult["nameContains"] = nameContains;
                if (!string.IsNullOrEmpty(pathContains))
                    filteredResult["pathContains"] = pathContains;
                return filteredResult;
            }

            int nodeCount = 0;
            var hierarchy = new List<object>();
            int totalAvailableNodes = CountAllObjects(startObjects);

            foreach (var root in startObjects)
            {
                var node = BuildHierarchyNode(root, 0, maxDepth, ref nodeCount, maxNodes);
                if (node != null)
                    hierarchy.Add(node);
                if (nodeCount >= maxNodes)
                    break;
            }

            var result = new Dictionary<string, object>
            {
                { "scene", scene.name },
                { "hierarchy", hierarchy },
                { "totalSceneObjects", totalSceneObjects },
                { "totalAvailableNodes", totalAvailableNodes },
                { "returnedNodes", nodeCount },
                { "maxNodes", maxNodes },
            };

            if (nodeCount < totalAvailableNodes)
            {
                result["truncated"] = true;
                result["message"] = $"Hierarchy truncated at {nodeCount} nodes ({totalAvailableNodes} available under the selected root). " +
                    "Use parentPath to explore specific subtrees, or increase maxNodes.";
            }
            else
            {
                result["truncated"] = false;
            }

            if (!string.IsNullOrEmpty(parentPath))
                result["parentPath"] = parentPath;

            return result;
        }

        private static void CollectComponentMatches(GameObject go, Type componentType, string nameContains,
            string pathContains, int offset, int maxResults, List<object> matches, ref int totalMatches)
        {
            string path = GetGameObjectPath(go.transform);
            bool nameMatches = string.IsNullOrEmpty(nameContains) ||
                               go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0;
            bool pathMatches = string.IsNullOrEmpty(pathContains) ||
                               path.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) >= 0;
            if (nameMatches && pathMatches && go.GetComponent(componentType) != null)
            {
                int matchIndex = totalMatches++;
                if (matchIndex >= offset && matches.Count < maxResults)
                {
                    var components = new List<string>();
                    foreach (var component in go.GetComponents<Component>())
                    {
                        if (component != null)
                            components.Add(component.GetType().Name);
                    }

                    matches.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "path", path },
                        { "instanceId", MCPObjectId.Get(go) },
                        { "active", go.activeSelf },
                        { "components", components },
                        { "position", VectorToDict(go.transform.position) },
                    });
                }
            }

            foreach (Transform child in go.transform)
                CollectComponentMatches(child.gameObject, componentType, nameContains, pathContains,
                    offset, maxResults, matches, ref totalMatches);
        }

        private static string GetGameObjectPath(Transform transform)
        {
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }

        /// <summary>Count all GameObjects recursively without building the full hierarchy.</summary>
        private static int CountAllObjects(GameObject[] roots)
        {
            int count = 0;
            foreach (var root in roots)
                CountRecursive(root, ref count);
            return count;
        }

        private static void CountRecursive(GameObject go, ref int count)
        {
            count++;
            foreach (Transform child in go.transform)
                CountRecursive(child.gameObject, ref count);
        }

        private static Dictionary<string, object> BuildHierarchyNode(
            GameObject go, int depth, int maxDepth, ref int nodeCount, int maxNodes)
        {
            if (nodeCount >= maxNodes)
                return null;

            nodeCount++;

            var components = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "instanceId", MCPObjectId.Get(go) },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
                { "position", VectorToDict(go.transform.position) },
            };

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (nodeCount >= maxNodes)
                    {
                        // Record how many children we couldn't include
                        node["childCount"] = go.transform.childCount;
                        node["childrenIncluded"] = children.Count;
                        node["childrenTruncated"] = true;
                        break;
                    }
                    var childNode = BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth, ref nodeCount, maxNodes);
                    if (childNode != null)
                        children.Add(childNode);
                }
                if (children.Count > 0)
                    node["children"] = children;
                if (!node.ContainsKey("childCount"))
                    node["childCount"] = go.transform.childCount;
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private static Dictionary<string, object> VectorToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }
    }
}
