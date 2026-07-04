using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Direct prefab asset editing — browse hierarchy, get/set properties, wire references,
    /// add/remove components and children on prefab assets without needing a scene instance.
    /// Every operation is atomic: load → modify → save → unload.
    /// </summary>
    public static class MCPPrefabAssetCommands
    {
        // ─── Hierarchy ───

        /// <summary>
        /// Get the full hierarchy tree of a prefab asset.
        /// </summary>
        public static object GetHierarchy(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            int maxDepth = args.ContainsKey("maxDepth") ? Convert.ToInt32(args["maxDepth"]) : 10;

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var hierarchy = BuildHierarchyNode(root, 0, maxDepth);
                return new Dictionary<string, object>
                {
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "hierarchy", hierarchy },
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Component Properties ───

        /// <summary>
        /// Read all properties from a component on a GameObject inside a prefab asset.
        /// </summary>
        public static object GetComponentProperties(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.GetComponent(type);
                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}'" };

                var serialized = new SerializedObject(component);
                var properties = new List<Dictionary<string, object>>();

                var iterator = serialized.GetIterator();
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        properties.Add(new Dictionary<string, object>
                        {
                            { "name", iterator.name },
                            { "displayName", iterator.displayName },
                            { "type", iterator.propertyType.ToString() },
                            { "value", MCPComponentCommands.GetSerializedValue(iterator) },
                            { "editable", iterator.editable },
                        });
                    } while (iterator.NextVisible(false));
                }

                return new Dictionary<string, object>
                {
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "prefabPath", prefabPath ?? "" },
                    { "component", componentType },
                    { "properties", properties },
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Set a component property on a GameObject inside a prefab asset.
        /// </summary>
        public static object SetComponentProperty(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            string propertyName = GetString(args, "propertyName");

            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };
            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };
            if (!args.ContainsKey("value"))
                return new { error = "value is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.GetComponent(type);
                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}'" };

                var serialized = new SerializedObject(component);
                var prop = serialized.FindProperty(propertyName);
                if (prop == null)
                    return new { error = $"Property '{propertyName}' not found on '{componentType}'" };

                MCPComponentCommands.SetSerializedValue(prop, args["value"]);
                serialized.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", componentType },
                    { "property", propertyName },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set property: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Components ───

        /// <summary>
        /// Add a component to a GameObject inside a prefab asset.
        /// </summary>
        public static object AddComponent(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.AddComponent(type);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", component.GetType().Name },
                    { "fullType", component.GetType().FullName },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to add component: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        public static void AddComponentDeferred(Dictionary<string, object> args, Action<object> resolve)
        {
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
            {
                resolve(new { error = "componentType is required" });
                return;
            }

            if (GetBool(args, "waitForType", true) == false)
            {
                resolve(AddComponent(args));
                return;
            }

            int timeoutMs = Math.Max(1, GetInt(args, "typeResolveTimeoutMs", 30000));
            int stableMs = Math.Max(0, GetInt(args, "typeResolveStableMs", 500));
            bool refreshAssets = GetBool(args, "refreshAssets", true);
            double startTime = EditorApplication.timeSinceStartup;
            double stableStartTime = -1;
            bool refreshRequested = false;

            EditorApplication.CallbackFunction tick = null;
            Action<object> complete = result =>
            {
                if (tick != null)
                    EditorApplication.update -= tick;
                resolve(result);
            };

            tick = () =>
            {
                try
                {
                    if (refreshAssets && refreshRequested == false)
                    {
                        refreshRequested = true;
                        AssetDatabase.Refresh();
                    }

                    bool editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                    Type resolvedType = MCPComponentCommands.FindType(componentType);

                    if (resolvedType != null && editorBusy == false)
                    {
                        if (stableStartTime < 0)
                            stableStartTime = EditorApplication.timeSinceStartup;

                        double stableElapsedMs = (EditorApplication.timeSinceStartup - stableStartTime) * 1000d;
                        if (stableElapsedMs >= stableMs)
                        {
                            complete(AddComponent(args));
                            return;
                        }
                    }
                    else
                    {
                        stableStartTime = -1;
                    }

                    double elapsedMs = (EditorApplication.timeSinceStartup - startTime) * 1000d;
                    if (elapsedMs >= timeoutMs)
                    {
                        complete(new Dictionary<string, object>
                        {
                            { "error", $"Type '{componentType}' not found after waiting {timeoutMs} ms" },
                            { "typeResolution", new Dictionary<string, object>
                                {
                                    { "componentType", componentType },
                                    { "elapsedMs", (int)elapsedMs },
                                    { "timeoutMs", timeoutMs },
                                    { "refreshedAssets", refreshRequested },
                                    { "isCompiling", EditorApplication.isCompiling },
                                    { "isUpdating", EditorApplication.isUpdating },
                                    { "likelyReason", EditorApplication.isCompiling || EditorApplication.isUpdating ? "unity_busy" : "type_not_found" },
                                }
                            },
                        });
                    }
                }
                catch (Exception ex)
                {
                    complete(new { error = $"Failed while waiting for component type: {ex.Message}", stackTrace = ex.StackTrace });
                }
            };

            EditorApplication.update += tick;
            tick();
        }

        /// <summary>
        /// Remove a component from a GameObject inside a prefab asset.
        /// </summary>
        public static object RemoveComponent(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            int index = args.ContainsKey("index") ? Convert.ToInt32(args["index"]) : 0;

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var components = go.GetComponents(type);
                if (components == null || index >= components.Length)
                    return new { error = $"Component '{componentType}' at index {index} not found on '{go.name}'" };

                UnityEngine.Object.DestroyImmediate(components[index]);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "removedComponent", componentType },
                    { "index", index },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to remove component: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Reference Wiring ───

        /// <summary>
        /// Wire an ObjectReference property on a component inside a prefab asset.
        /// Supports references to assets (by path) and to other GameObjects within the same prefab.
        /// </summary>
        public static object SetReference(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            string propertyName = GetString(args, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            string referenceAssetPath = GetString(args, "referenceAssetPath");
            string referencePrefabPath = GetString(args, "referencePrefabPath");
            string referenceComponentType = GetString(args, "referenceComponentType");
            bool clearRef = args.ContainsKey("clear") && Convert.ToBoolean(args["clear"]);

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                // Find component (auto-search if componentType not specified)
                Component component = null;
                if (!string.IsNullOrEmpty(componentType))
                {
                    Type type = MCPComponentCommands.FindType(componentType);
                    if (type != null) component = go.GetComponent(type);
                }
                else
                {
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var so = new SerializedObject(comp);
                        if (so.FindProperty(propertyName) != null)
                        {
                            component = comp;
                            break;
                        }
                    }
                }

                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}', or no component has property '{propertyName}'" };

                var serialized = new SerializedObject(component);
                var prop = serialized.FindProperty(propertyName);
                if (prop == null)
                    return new { error = $"Property '{propertyName}' not found" };

                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                    return new { error = $"Property '{propertyName}' is not an ObjectReference (type: {prop.propertyType})" };

                // Resolve reference
                UnityEngine.Object targetRef = null;
                string refDescription = "null (cleared)";

                if (clearRef)
                {
                    prop.objectReferenceValue = null;
                }
                else if (!string.IsNullOrEmpty(referenceAssetPath))
                {
                    targetRef = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(referenceAssetPath);
                    if (targetRef == null)
                        return new { error = $"Asset not found at '{referenceAssetPath}'" };

                    prop.objectReferenceValue = targetRef;
                    refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                }
                else if (!string.IsNullOrEmpty(referencePrefabPath))
                {
                    var refGo = FindInPrefab(root, referencePrefabPath);
                    if (refGo == null)
                        return new { error = $"GameObject '{referencePrefabPath}' not found in prefab" };

                    if (!string.IsNullOrEmpty(referenceComponentType))
                    {
                        Type refType = MCPComponentCommands.FindType(referenceComponentType);
                        if (refType == null)
                            return new { error = $"Type '{referenceComponentType}' not found" };

                        targetRef = refGo.GetComponent(refType);
                        if (targetRef == null)
                            return new { error = $"Component '{referenceComponentType}' not found on '{refGo.name}'" };
                    }
                    else
                    {
                        targetRef = refGo;
                    }

                    prop.objectReferenceValue = targetRef;
                    refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                }
                else
                {
                    return new { error = "Provide referenceAssetPath, referencePrefabPath, or clear=true" };
                }

                serialized.ApplyModifiedProperties();
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", component.GetType().Name },
                    { "property", propertyName },
                    { "reference", refDescription },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set reference: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Hierarchy Modification ───

        /// <summary>
        /// Create a new child GameObject inside a prefab asset.
        /// </summary>
        public static object AddGameObject(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string parentPrefabPath = GetString(args, "parentPrefabPath");
            string name = GetString(args, "name");
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required" };

            string primitiveType = GetString(args, "primitiveType");

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var parent = FindInPrefab(root, parentPrefabPath);
                if (parent == null)
                    return new { error = $"Parent '{parentPrefabPath}' not found in prefab" };

                GameObject newGo;
                if (!string.IsNullOrEmpty(primitiveType) && Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
                {
                    newGo = GameObject.CreatePrimitive(pt);
                    newGo.name = name;
                }
                else
                {
                    newGo = new GameObject(name);
                }

                newGo.transform.SetParent(parent.transform, false);

                // Set transform if provided
                if (args.ContainsKey("position"))
                    newGo.transform.localPosition = ParseVector3(args["position"]);
                if (args.ContainsKey("rotation"))
                    newGo.transform.localEulerAngles = ParseVector3(args["rotation"]);
                if (args.ContainsKey("scale"))
                    newGo.transform.localScale = ParseVector3(args["scale"]);

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "createdGameObject", name },
                    { "parent", string.IsNullOrEmpty(parentPrefabPath) ? "root" : parentPrefabPath },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to add GameObject: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Instantiate a prefab asset as a child inside another prefab asset.
        /// </summary>
        public static object InstantiatePrefab(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string sourcePrefabPath = GetString(args, "sourcePrefabPath");
            if (string.IsNullOrEmpty(sourcePrefabPath))
                return new { error = "sourcePrefabPath is required" };

            string parentPrefabPath = GetString(args, "parentPrefabPath");
            string name = GetString(args, "name");
            int siblingIndex = GetInt(args, "siblingIndex", -1);

            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (sourcePrefab == null)
                return new { error = $"Source prefab not found at '{sourcePrefabPath}'" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var parent = FindInPrefab(root, parentPrefabPath);
                if (parent == null)
                    return new { error = $"Parent '{parentPrefabPath}' not found in prefab" };

                var instance = PrefabUtility.InstantiatePrefab(sourcePrefab, root.scene) as GameObject;
                if (instance == null)
                    return new { error = $"Failed to instantiate prefab '{sourcePrefabPath}'" };

                instance.transform.SetParent(parent.transform, false);

                if (string.IsNullOrEmpty(name) == false)
                    instance.name = name;

                if (args.ContainsKey("position"))
                    instance.transform.localPosition = ParseVector3(args["position"]);
                if (args.ContainsKey("rotation"))
                    instance.transform.localEulerAngles = ParseVector3(args["rotation"]);
                if (args.ContainsKey("scale"))
                    instance.transform.localScale = ParseVector3(args["scale"]);

                if (siblingIndex >= 0)
                    instance.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.transform.childCount - 1));

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "sourcePrefabPath", sourcePrefabPath },
                    { "createdGameObject", instance.name },
                    { "prefabPath", GetPrefabPath(root, instance) },
                    { "parent", string.IsNullOrEmpty(parentPrefabPath) ? "root" : parentPrefabPath },
                    { "siblingIndex", instance.transform.GetSiblingIndex() },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to instantiate prefab: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Delete a child GameObject from a prefab asset.
        /// Cannot delete the root GameObject.
        /// </summary>
        public static object RemoveGameObject(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required (cannot delete root)" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                if (go == root)
                    return new { error = "Cannot delete the root GameObject of a prefab" };

                string deletedName = go.name;
                UnityEngine.Object.DestroyImmediate(go);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "deletedGameObject", deletedName },
                    { "prefabPath", prefabPath },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to remove GameObject: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Move or reorder a child GameObject inside a prefab asset.
        /// </summary>
        public static object MoveGameObject(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            string prefabPath = GetString(args, "prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required (cannot move root)" };

            string newParentPrefabPath = GetString(args, "newParentPrefabPath");
            int siblingIndex = GetInt(args, "siblingIndex", -1);
            bool worldPositionStays = GetBool(args, "worldPositionStays", false);

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                if (go == root)
                    return new { error = "Cannot move the root GameObject of a prefab" };

                var newParent = FindInPrefab(root, newParentPrefabPath);
                if (newParent == null)
                    return new { error = $"New parent '{newParentPrefabPath}' not found in prefab" };

                string oldPath = GetPrefabPath(root, go);
                string oldParentPath = go.transform.parent != null
                    ? GetPrefabPath(root, go.transform.parent.gameObject)
                    : "";
                int oldSiblingIndex = go.transform.GetSiblingIndex();

                go.transform.SetParent(newParent.transform, worldPositionStays);
                if (siblingIndex >= 0)
                    go.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, newParent.transform.childCount - 1));

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "oldPath", oldPath },
                    { "newPath", GetPrefabPath(root, go) },
                    { "oldParent", oldParentPath },
                    { "newParent", string.IsNullOrEmpty(newParentPrefabPath) ? "root" : newParentPrefabPath },
                    { "oldSiblingIndex", oldSiblingIndex },
                    { "newSiblingIndex", go.transform.GetSiblingIndex() },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to move GameObject: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Find GameObjects in a prefab asset by name/path, component type, and optional serialized property value.
        /// </summary>
        public static object Find(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string name = GetString(args, "name");
            string nameContains = GetString(args, "nameContains");
            string pathContains = GetString(args, "pathContains");
            string componentType = GetString(args, "componentType");
            string propertyName = GetString(args, "propertyName");
            bool hasPropertyValue = args != null && args.ContainsKey("propertyValue");
            object propertyValue = hasPropertyValue ? args["propertyValue"] : null;
            int maxResults = GetInt(args, "maxResults", 50);

            Type type = null;
            if (string.IsNullOrEmpty(componentType) == false)
            {
                type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };
            }

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var results = new List<Dictionary<string, object>>();
                bool truncated = false;

                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = transform.gameObject;
                    string prefabPath = GetPrefabPath(root, go);

                    if (string.IsNullOrEmpty(name) == false && go.name != name)
                        continue;
                    if (string.IsNullOrEmpty(nameContains) == false &&
                        go.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (string.IsNullOrEmpty(pathContains) == false &&
                        prefabPath.IndexOf(pathContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var components = type != null ? go.GetComponents(type) : go.GetComponents<Component>();
                    bool requiresComponentMatch = type != null || string.IsNullOrEmpty(propertyName) == false;

                    if (requiresComponentMatch == false)
                    {
                        if (TryAddFindResult(results, maxResults, ref truncated, root, go, null, null, null))
                            continue;
                        break;
                    }

                    foreach (var component in components)
                    {
                        if (component == null)
                            continue;

                        SerializedProperty matchedProperty = null;
                        object matchedValue = null;

                        if (string.IsNullOrEmpty(propertyName) == false)
                        {
                            var serialized = new SerializedObject(component);
                            matchedProperty = serialized.FindProperty(propertyName);
                            if (matchedProperty == null)
                                continue;

                            matchedValue = MCPComponentCommands.GetSerializedValue(matchedProperty);
                            if (hasPropertyValue && SerializedValueMatches(matchedValue, propertyValue) == false)
                                continue;
                        }

                        if (TryAddFindResult(results, maxResults, ref truncated, root, go, component,
                                propertyName, matchedValue))
                            continue;

                        return BuildFindResponse(root, assetPath, results, truncated);
                    }
                }

                return BuildFindResponse(root, assetPath, results, truncated);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Variant Management ───

        /// <summary>
        /// Get variant info for a prefab: is it a variant? what's the base? Also list all known variants of a base prefab.
        /// </summary>
        public static object GetVariantInfo(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            var assetType = PrefabUtility.GetPrefabAssetType(asset);
            bool isVariant = assetType == PrefabAssetType.Variant;

            var result = new Dictionary<string, object>
            {
                { "prefab", asset.name },
                { "assetPath", assetPath },
                { "isVariant", isVariant },
                { "assetType", assetType.ToString() },
            };

            if (isVariant)
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                if (basePrefab != null)
                {
                    string basePath = AssetDatabase.GetAssetPath(basePrefab);
                    result["basePrefabPath"] = basePath;
                    result["basePrefabName"] = basePrefab.name;
                }
            }

            // Find all variants of this prefab (or of the base if this is already a variant)
            string searchBasePath = assetPath;
            if (isVariant)
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                if (basePrefab != null)
                    searchBasePath = AssetDatabase.GetAssetPath(basePrefab);
            }

            var variants = new List<Dictionary<string, object>>();
            var allPrefabs = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in allPrefabs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == searchBasePath) continue;

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                if (PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Variant)
                    continue;

                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                if (source != null && AssetDatabase.GetAssetPath(source) == searchBasePath)
                {
                    variants.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "assetPath", path },
                    });
                }
            }

            result["basePrefab"] = searchBasePath;
            result["variants"] = variants;
            result["variantCount"] = variants.Count;

            return result;
        }

        /// <summary>
        /// Compare a variant to its base prefab — list all property overrides, added/removed components, added/removed GameObjects.
        /// </summary>
        public static object CompareVariantToBase(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant)
                return new { error = $"'{assetPath}' is not a variant prefab" };

            // Instantiate to read overrides (PrefabUtility override APIs need an instance or asset)
            var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null)
                return new { error = "Failed to instantiate variant for comparison" };

            try
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                string basePath = basePrefab != null ? AssetDatabase.GetAssetPath(basePrefab) : "unknown";

                // Property overrides
                var propertyOverrides = PrefabUtility.GetPropertyModifications(asset);
                var overrideList = new List<Dictionary<string, object>>();
                if (propertyOverrides != null)
                {
                    foreach (var mod in propertyOverrides)
                    {
                        if (mod.target == null) continue;
                        // Skip internal Transform position/rotation on root (always present)
                        overrideList.Add(new Dictionary<string, object>
                        {
                            { "targetType", mod.target.GetType().Name },
                            { "targetName", mod.target.name },
                            { "propertyPath", mod.propertyPath },
                            { "value", mod.value ?? "null" },
                        });
                    }
                }

                // Added components
                var addedComponents = PrefabUtility.GetAddedComponents(instance);
                var addedCompList = new List<Dictionary<string, object>>();
                foreach (var added in addedComponents)
                {
                    addedCompList.Add(new Dictionary<string, object>
                    {
                        { "componentType", added.instanceComponent.GetType().Name },
                        { "gameObject", added.instanceComponent.gameObject.name },
                    });
                }

                // Removed components
                var removedComponents = PrefabUtility.GetRemovedComponents(instance);
                var removedCompList = new List<Dictionary<string, object>>();
                foreach (var removed in removedComponents)
                {
                    removedCompList.Add(new Dictionary<string, object>
                    {
                        { "componentType", removed.assetComponent.GetType().Name },
                        { "gameObject", removed.assetComponent.gameObject.name },
                    });
                }

                // Added GameObjects
                var addedGOs = PrefabUtility.GetAddedGameObjects(instance);
                var addedGOList = new List<Dictionary<string, object>>();
                foreach (var added in addedGOs)
                {
                    addedGOList.Add(new Dictionary<string, object>
                    {
                        { "name", added.instanceGameObject.name },
                        { "childCount", added.instanceGameObject.transform.childCount },
                    });
                }

                // Removed GameObjects
                var removedGOList = new List<Dictionary<string, object>>();
#if UNITY_2022_1_OR_NEWER
                var removedGOs = PrefabUtility.GetRemovedGameObjects(instance);
                foreach (var removed in removedGOs)
                {
                    removedGOList.Add(new Dictionary<string, object>
                    {
                        { "name", removed.assetGameObject.name },
                    });
                }
#else
                // Fallback for Unity < 2022.1: compare asset children vs instance children
                var assetSource = PrefabUtility.GetCorrespondingObjectFromSource(instance);
                if (assetSource != null)
                {
                    foreach (Transform assetChild in assetSource.transform)
                    {
                        var correspondingInInstance = instance.transform.Find(assetChild.name);
                        if (correspondingInInstance == null)
                        {
                            removedGOList.Add(new Dictionary<string, object>
                            {
                                { "name", assetChild.name },
                            });
                        }
                    }
                }
#endif

                return new Dictionary<string, object>
                {
                    { "variant", asset.name },
                    { "variantPath", assetPath },
                    { "basePrefab", basePrefab != null ? basePrefab.name : "unknown" },
                    { "basePrefabPath", basePath },
                    { "propertyOverrides", overrideList },
                    { "propertyOverrideCount", overrideList.Count },
                    { "addedComponents", addedCompList },
                    { "removedComponents", removedCompList },
                    { "addedGameObjects", addedGOList },
                    { "removedGameObjects", removedGOList },
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Apply a specific override from a variant back to its base prefab, or apply all overrides.
        /// </summary>
        public static object ApplyVariantOverride(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            bool applyAll = args.ContainsKey("applyAll") && Convert.ToBoolean(args["applyAll"]);
            string propertyPath = GetString(args, "propertyPath");
            string targetComponentType = GetString(args, "targetComponentType");
            string targetGameObject = GetString(args, "targetGameObject");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant)
                return new { error = $"'{assetPath}' is not a variant prefab" };

            var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null)
                return new { error = "Failed to instantiate variant" };

            try
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                string basePath = basePrefab != null ? AssetDatabase.GetAssetPath(basePrefab) : null;
                if (string.IsNullOrEmpty(basePath))
                    return new { error = "Could not determine base prefab path" };
                var beforeSnapshot = CaptureAssetText(basePath);

                int appliedCount = 0;

                if (applyAll)
                {
                    // Apply everything to base
                    PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
                    appliedCount = -1; // signals "all"
                }
                else
                {
                    // Apply specific overrides matching filters
                    var objectOverrides = PrefabUtility.GetObjectOverrides(instance);
                    foreach (var ov in objectOverrides)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetComponentType))
                        {
                            var comp = ov.instanceObject as Component;
                            if (comp == null || comp.GetType().Name != targetComponentType)
                                matches = false;
                        }
                        if (!string.IsNullOrEmpty(targetGameObject))
                        {
                            var comp = ov.instanceObject as Component;
                            var go = ov.instanceObject as GameObject;
                            string goName = comp != null ? comp.gameObject.name : go != null ? go.name : "";
                            if (goName != targetGameObject)
                                matches = false;
                        }
                        if (matches)
                        {
                            ov.Apply(basePath, InteractionMode.AutomatedAction);
                            appliedCount++;
                        }
                    }

                    // Apply added components
                    var addedComps = PrefabUtility.GetAddedComponents(instance);
                    foreach (var ac in addedComps)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetComponentType) && ac.instanceComponent.GetType().Name != targetComponentType)
                            matches = false;
                        if (!string.IsNullOrEmpty(targetGameObject) && ac.instanceComponent.gameObject.name != targetGameObject)
                            matches = false;
                        if (matches)
                        {
                            ac.Apply(basePath, InteractionMode.AutomatedAction);
                            appliedCount++;
                        }
                    }

                    // Apply added GameObjects
                    var addedGOs = PrefabUtility.GetAddedGameObjects(instance);
                    foreach (var ag in addedGOs)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetGameObject) && ag.instanceGameObject.name != targetGameObject)
                            matches = false;
                        if (matches)
                        {
                            ag.Apply(basePath, InteractionMode.AutomatedAction);
                            appliedCount++;
                        }
                    }
                }

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "variant", asset.name },
                    { "basePrefab", basePrefab != null ? basePrefab.name : "unknown" },
                    { "appliedCount", appliedCount == -1 ? "all" : (object)appliedCount },
                };
                AddPrefabFileDiff(result, beforeSnapshot, basePath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to apply overrides: {ex.Message}" };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Revert a variant's overrides so it matches the base prefab again.
        /// Can revert all or only specific overrides by component/gameObject filter.
        /// </summary>
        public static object RevertVariantOverride(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };
            var beforeSnapshot = CaptureAssetText(assetPath);

            bool revertAll = args.ContainsKey("revertAll") && Convert.ToBoolean(args["revertAll"]);
            string targetComponentType = GetString(args, "targetComponentType");
            string targetGameObject = GetString(args, "targetGameObject");

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant)
                return new { error = $"'{assetPath}' is not a variant prefab" };

            var instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null)
                return new { error = "Failed to instantiate variant" };

            try
            {
                int revertedCount = 0;

                if (revertAll)
                {
                    PrefabUtility.RevertPrefabInstance(instance, InteractionMode.AutomatedAction);
                    revertedCount = -1;
                }
                else
                {
                    var objectOverrides = PrefabUtility.GetObjectOverrides(instance);
                    foreach (var ov in objectOverrides)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetComponentType))
                        {
                            var comp = ov.instanceObject as Component;
                            if (comp == null || comp.GetType().Name != targetComponentType)
                                matches = false;
                        }
                        if (!string.IsNullOrEmpty(targetGameObject))
                        {
                            var comp = ov.instanceObject as Component;
                            var go = ov.instanceObject as GameObject;
                            string goName = comp != null ? comp.gameObject.name : go != null ? go.name : "";
                            if (goName != targetGameObject)
                                matches = false;
                        }
                        if (matches)
                        {
                            ov.Revert();
                            revertedCount++;
                        }
                    }

                    var addedComps = PrefabUtility.GetAddedComponents(instance);
                    foreach (var ac in addedComps)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetComponentType) && ac.instanceComponent.GetType().Name != targetComponentType)
                            matches = false;
                        if (!string.IsNullOrEmpty(targetGameObject) && ac.instanceComponent.gameObject.name != targetGameObject)
                            matches = false;
                        if (matches)
                        {
                            ac.Revert();
                            revertedCount++;
                        }
                    }

                    var addedGOs = PrefabUtility.GetAddedGameObjects(instance);
                    foreach (var ag in addedGOs)
                    {
                        bool matches = true;
                        if (!string.IsNullOrEmpty(targetGameObject) && ag.instanceGameObject.name != targetGameObject)
                            matches = false;
                        if (matches)
                        {
                            ag.Revert();
                            revertedCount++;
                        }
                    }
                }

                // Save the reverted variant back to disk
                PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "variant", asset.name },
                    { "revertedCount", revertedCount == -1 ? "all" : (object)revertedCount },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to revert overrides: {ex.Message}" };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Transfer (copy) overrides from one variant to another variant of the same base.
        /// Reads properties from source variant and applies them to target variant.
        /// </summary>
        public static object TransferVariantOverrides(Dictionary<string, object> args)
        {
            string sourceAssetPath = GetString(args, "sourceAssetPath");
            string targetAssetPath = GetString(args, "targetAssetPath");

            if (string.IsNullOrEmpty(sourceAssetPath))
                return new { error = "sourceAssetPath is required" };
            if (string.IsNullOrEmpty(targetAssetPath))
                return new { error = "targetAssetPath is required" };
            var beforeSnapshot = CaptureAssetText(targetAssetPath);

            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
            var targetAsset = AssetDatabase.LoadAssetAtPath<GameObject>(targetAssetPath);

            if (sourceAsset == null) return new { error = $"Source prefab not found at '{sourceAssetPath}'" };
            if (targetAsset == null) return new { error = $"Target prefab not found at '{targetAssetPath}'" };

            // Get source overrides
            var sourceMods = PrefabUtility.GetPropertyModifications(sourceAsset);
            if (sourceMods == null || sourceMods.Length == 0)
                return new { error = "Source variant has no overrides to transfer" };

            // Filter by component/property if requested
            string filterComponentType = GetString(args, "filterComponentType");
            string filterPropertyPath = GetString(args, "filterPropertyPath");

            // Load target for editing
            var targetRoot = PrefabUtility.LoadPrefabContents(targetAssetPath);
            if (targetRoot == null)
                return new { error = "Failed to load target prefab for editing" };

            try
            {
                int transferred = 0;

                // Get the existing modifications on target
                var targetMods = PrefabUtility.GetPropertyModifications(targetAsset);
                var newMods = new List<PropertyModification>(targetMods ?? new PropertyModification[0]);

                foreach (var mod in sourceMods)
                {
                    if (mod.target == null) continue;

                    // Apply filters
                    if (!string.IsNullOrEmpty(filterComponentType) && mod.target.GetType().Name != filterComponentType)
                        continue;
                    if (!string.IsNullOrEmpty(filterPropertyPath) && !mod.propertyPath.Contains(filterPropertyPath))
                        continue;

                    // Check if this override already exists on target, replace or add
                    bool found = false;
                    for (int i = 0; i < newMods.Count; i++)
                    {
                        if (newMods[i].target != null &&
                            newMods[i].target.GetType() == mod.target.GetType() &&
                            newMods[i].propertyPath == mod.propertyPath)
                        {
                            newMods[i] = mod;
                            found = true;
                            break;
                        }
                    }
                    if (!found) newMods.Add(mod);
                    transferred++;
                }

                PrefabUtility.SetPropertyModifications(targetRoot, newMods.ToArray());
                PrefabUtility.SaveAsPrefabAsset(targetRoot, targetAssetPath);

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "source", sourceAsset.name },
                    { "target", targetAsset.name },
                    { "transferredOverrides", transferred },
                };
                AddPrefabFileDiff(result, beforeSnapshot, targetAssetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to transfer overrides: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(targetRoot);
            }
        }

        // ─── Helpers ───

        private static GameObject FindInPrefab(GameObject root, string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return root;

            Transform current = root.transform;
            foreach (var part in prefabPath.Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                current = current.Find(part);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        private static Dictionary<string, object> BuildHierarchyNode(GameObject go, int depth, int maxDepth)
        {
            var components = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
                { "localPosition", VectorToDict(go.transform.localPosition) },
                { "localRotation", VectorToDict(go.transform.localEulerAngles) },
                { "localScale", VectorToDict(go.transform.localScale) },
            };

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    children.Add(BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth));
                }
                node["children"] = children;
                node["childCount"] = go.transform.childCount;
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) ? args[key]?.ToString() : "";
        }

        private static void AddPrefabFileDiff(Dictionary<string, object> result,
            AssetTextSnapshot beforeSnapshot, string assetPath, Dictionary<string, object> args)
        {
            if (result == null || GetBool(args, "includePrefabFileDiff", true) == false)
                return;

            result["prefabFileDiff"] = BuildAssetTextDiff(beforeSnapshot, assetPath, args);
        }

        private static Dictionary<string, object> BuildAssetTextDiff(AssetTextSnapshot beforeSnapshot,
            string assetPath, Dictionary<string, object> args)
        {
            var afterSnapshot = CaptureAssetText(assetPath);
            int contextLines = Math.Max(0, GetInt(args, "prefabFileDiffContextLines", 2));
            int maxLines = Math.Max(1, GetInt(args, "prefabFileDiffMaxLines", 200));

            var result = new Dictionary<string, object>
            {
                { "assetPath", assetPath },
                { "absolutePath", afterSnapshot.AbsolutePath },
                { "existsBefore", beforeSnapshot.Exists },
                { "existsAfter", afterSnapshot.Exists },
                { "readErrorBefore", beforeSnapshot.ReadError ?? "" },
                { "readErrorAfter", afterSnapshot.ReadError ?? "" },
            };

            if (!string.IsNullOrEmpty(beforeSnapshot.ReadError) || !string.IsNullOrEmpty(afterSnapshot.ReadError))
            {
                result["changed"] = true;
                result["lines"] = new List<Dictionary<string, object>>();
                result["truncated"] = false;
                return result;
            }

            var beforeLines = SplitLines(beforeSnapshot.Text);
            var afterLines = SplitLines(afterSnapshot.Text);

            result["beforeLineCount"] = beforeLines.Length;
            result["afterLineCount"] = afterLines.Length;
            result["changed"] = beforeSnapshot.Text != afterSnapshot.Text;

            var lines = BuildChangedLineBlock(beforeLines, afterLines, contextLines, maxLines, out int changedLineCount,
                out bool truncated);
            result["changedLineCount"] = changedLineCount;
            result["returnedLineCount"] = lines.Count;
            result["truncated"] = truncated;
            result["lines"] = lines;
            return result;
        }

        private static List<Dictionary<string, object>> BuildChangedLineBlock(string[] beforeLines, string[] afterLines,
            int contextLines, int maxLines, out int changedLineCount, out bool truncated)
        {
            var lines = new List<Dictionary<string, object>>();
            truncated = false;

            int prefix = 0;
            int minCount = Math.Min(beforeLines.Length, afterLines.Length);
            while (prefix < minCount && beforeLines[prefix] == afterLines[prefix])
                prefix++;

            if (prefix == beforeLines.Length && prefix == afterLines.Length)
            {
                changedLineCount = 0;
                return lines;
            }

            int beforeEnd = beforeLines.Length - 1;
            int afterEnd = afterLines.Length - 1;
            while (beforeEnd >= prefix && afterEnd >= prefix && beforeLines[beforeEnd] == afterLines[afterEnd])
            {
                beforeEnd--;
                afterEnd--;
            }

            int contextBeforeStart = Math.Max(0, prefix - contextLines);
            for (int i = contextBeforeStart; i < prefix; i++)
                AddDiffLine(lines, maxLines, ref truncated, "context", i + 1, i + 1, beforeLines[i]);

            int removedCount = Math.Max(0, beforeEnd - prefix + 1);
            int addedCount = Math.Max(0, afterEnd - prefix + 1);
            changedLineCount = removedCount + addedCount;

            for (int i = prefix; i <= beforeEnd; i++)
                AddDiffLine(lines, maxLines, ref truncated, "removed", i + 1, null, beforeLines[i]);

            for (int i = prefix; i <= afterEnd; i++)
                AddDiffLine(lines, maxLines, ref truncated, "added", null, i + 1, afterLines[i]);

            int contextAfterEnd = Math.Min(afterLines.Length - 1, afterEnd + contextLines);
            for (int i = afterEnd + 1; i <= contextAfterEnd; i++)
                AddDiffLine(lines, maxLines, ref truncated, "context", null, i + 1, afterLines[i]);

            return lines;
        }

        private static void AddDiffLine(List<Dictionary<string, object>> lines, int maxLines, ref bool truncated,
            string type, int? beforeLine, int? afterLine, string text)
        {
            if (lines.Count >= maxLines)
            {
                truncated = true;
                return;
            }

            lines.Add(new Dictionary<string, object>
            {
                { "type", type },
                { "beforeLine", beforeLine.HasValue ? beforeLine.Value : (object)null },
                { "afterLine", afterLine.HasValue ? afterLine.Value : (object)null },
                { "text", text },
            });
        }

        private static AssetTextSnapshot CaptureAssetText(string assetPath)
        {
            var snapshot = new AssetTextSnapshot
            {
                AssetPath = assetPath,
                AbsolutePath = GetAbsoluteAssetPath(assetPath),
            };

            try
            {
                snapshot.Exists = File.Exists(snapshot.AbsolutePath);
                snapshot.Text = snapshot.Exists ? File.ReadAllText(snapshot.AbsolutePath) : "";
            }
            catch (Exception ex)
            {
                snapshot.ReadError = ex.Message;
                snapshot.Text = "";
            }

            return snapshot;
        }

        private static string GetAbsoluteAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "";

            if (Path.IsPathRooted(assetPath))
                return Path.GetFullPath(assetPath);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<string>();

            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private sealed class AssetTextSnapshot
        {
            public string AssetPath;
            public string AbsolutePath;
            public bool Exists;
            public string Text;
            public string ReadError;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || args.ContainsKey(key) == false || args[key] == null)
                return defaultValue;

            if (args[key] is bool value)
                return value;

            return bool.TryParse(args[key].ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || args.ContainsKey(key) == false || args[key] == null)
                return defaultValue;

            return int.TryParse(args[key].ToString(), out int value) ? value : defaultValue;
        }

        private static string GetPrefabPath(GameObject root, GameObject go)
        {
            if (root == go)
                return "";

            var names = new Stack<string>();
            Transform current = go.transform;
            while (current != null && current.gameObject != root)
            {
                names.Push(current.name);
                current = current.parent;
            }

            return current == null ? go.name : string.Join("/", names);
        }

        private static bool TryAddFindResult(List<Dictionary<string, object>> results, int maxResults,
            ref bool truncated, GameObject root, GameObject go, Component component, string propertyName,
            object propertyValue)
        {
            if (results.Count >= maxResults)
            {
                truncated = true;
                return false;
            }

            var result = new Dictionary<string, object>
            {
                { "name", go.name },
                { "prefabPath", GetPrefabPath(root, go) },
                { "active", go.activeSelf },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "localPosition", VectorToDict(go.transform.localPosition) },
                { "localRotation", VectorToDict(go.transform.localEulerAngles) },
                { "localScale", VectorToDict(go.transform.localScale) },
            };

            if (component != null)
            {
                result["component"] = component.GetType().Name;
                result["componentFullType"] = component.GetType().FullName;
            }

            if (string.IsNullOrEmpty(propertyName) == false)
            {
                result["propertyName"] = propertyName;
                result["propertyValue"] = propertyValue;
            }

            results.Add(result);
            return true;
        }

        private static Dictionary<string, object> BuildFindResponse(GameObject root, string assetPath,
            List<Dictionary<string, object>> results, bool truncated)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "prefab", root.name },
                { "assetPath", assetPath },
                { "count", results.Count },
                { "truncated", truncated },
                { "results", results },
            };
        }

        private static bool SerializedValueMatches(object actual, object expected)
        {
            if (expected == null)
                return actual == null;
            if (actual == null)
                return string.Equals(expected.ToString(), "null", StringComparison.OrdinalIgnoreCase);

            if (actual is Dictionary<string, object> || expected is Dictionary<string, object>)
                return string.Equals(MiniJson.Serialize(actual), MiniJson.Serialize(expected),
                    StringComparison.OrdinalIgnoreCase);

            if (actual is System.Collections.IList || expected is System.Collections.IList)
                return string.Equals(MiniJson.Serialize(actual), MiniJson.Serialize(expected),
                    StringComparison.OrdinalIgnoreCase);

            return string.Equals(actual.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> VectorToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }

        private static Vector3 ParseVector3(object value)
        {
            if (value is Dictionary<string, object> d)
            {
                return new Vector3(
                    d.ContainsKey("x") ? Convert.ToSingle(d["x"]) : 0f,
                    d.ContainsKey("y") ? Convert.ToSingle(d["y"]) : 0f,
                    d.ContainsKey("z") ? Convert.ToSingle(d["z"]) : 0f
                );
            }
            return Vector3.zero;
        }
    }
}
