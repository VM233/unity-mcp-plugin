using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

            int maxDepth = Math.Max(0, Math.Min(GetInt(args, "maxDepth", 10), 50));
            int maxNodes = Math.Max(1, Math.Min(GetInt(args, "maxNodes", 250), 2000));
            string prefabPath = GetString(args, "prefabPath");

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var hierarchyRoot = FindInPrefab(root, prefabPath);
                if (hierarchyRoot == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                int totalNodes = CountHierarchyNodes(hierarchyRoot);
                int returnedNodes = 0;
                var hierarchy = BuildHierarchyNode(hierarchyRoot, 0, maxDepth, ref returnedNodes, maxNodes);
                var result = new Dictionary<string, object>
                {
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "hierarchy", hierarchy },
                    { "returnedNodes", returnedNodes },
                    { "totalNodes", totalNodes },
                    { "maxNodes", maxNodes },
                    { "truncated", returnedNodes < totalNodes },
                };
                if (!string.IsNullOrEmpty(prefabPath))
                    result["prefabPath"] = prefabPath;
                return result;
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

                SavePrefabAssetNormalized(root, assetPath, BuildExplicitYamlPropertyRoots(propertyName));

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
                SavePrefabAssetNormalized(root, assetPath);

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
                SavePrefabAssetNormalized(root, assetPath);

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

        /// <summary>
        /// Move a component between GameObjects inside one prefab asset in a single load/save transaction.
        /// </summary>
        public static object MoveComponent(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string sourcePrefabPath = GetString(args, "sourcePrefabPath");
            string targetPrefabPath = GetString(args, "targetPrefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            int componentIndex = GetInt(args, "componentIndex", GetInt(args, "index", 0));
            if (componentIndex < 0)
                return new { error = "componentIndex must be zero or greater" };

            var beforeSnapshot = CaptureAssetText(assetPath);
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            bool saved = false;
            try
            {
                var source = FindInPrefab(root, sourcePrefabPath);
                if (source == null)
                    return new { error = $"Source GameObject '{sourcePrefabPath}' not found in prefab" };

                var target = FindInPrefab(root, targetPrefabPath);
                if (target == null)
                    return new { error = $"Target GameObject '{targetPrefabPath}' not found in prefab" };
                if (source == target)
                    return new { error = "Source and target GameObjects must be different" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };
                if (typeof(Transform).IsAssignableFrom(type))
                    return new { error = "Transform components cannot be moved" };

                var sourceComponents = source.GetComponents(type);
                if (componentIndex >= sourceComponents.Length)
                {
                    return new
                    {
                        error = $"Component '{componentType}' at index {componentIndex} not found on '{source.name}'"
                    };
                }

                var sourceComponent = sourceComponents[componentIndex];
                Type movedComponentType = sourceComponent.GetType();
                int targetComponentCount = target.GetComponents(type).Length;
                if (!UnityEditorInternal.ComponentUtility.CopyComponent(sourceComponent) ||
                    !UnityEditorInternal.ComponentUtility.PasteComponentAsNew(target))
                {
                    return new { error = $"Failed to copy component '{componentType}' to '{target.name}'" };
                }

                var targetComponents = target.GetComponents(type);
                if (targetComponents.Length != targetComponentCount + 1)
                {
                    return new { error = $"Component '{componentType}' was not added exactly once to '{target.name}'" };
                }

                var movedComponent = targetComponents[targetComponentCount];
                int remappedReferenceCount = RemapComponentReferences(root, sourceComponent, movedComponent);

                UnityEngine.Object.DestroyImmediate(sourceComponent);
                if (source.GetComponents(type).Length != sourceComponents.Length - 1)
                    return new { error = $"Failed to remove component '{componentType}' from '{source.name}'" };

                if (SavePrefabAssetNormalized(root, assetPath) == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{assetPath}'.");
                saved = true;

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "sourcePrefabPath", GetPrefabPath(root, source) },
                    { "targetPrefabPath", GetPrefabPath(root, target) },
                    { "component", movedComponentType.Name },
                    { "fullType", movedComponentType.FullName },
                    { "componentIndex", componentIndex },
                    { "remappedReferenceCount", remappedReferenceCount },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to move component: {ex.Message}", stackTrace = ex.StackTrace };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
                if (!saved)
                    RestoreAssetSnapshot(beforeSnapshot);
            }
        }

        private static int RemapComponentReferences(GameObject prefabRoot, Component sourceComponent,
            Component movedComponent)
        {
            int remappedReferenceCount = 0;
            foreach (var owner in prefabRoot.GetComponentsInChildren<Component>(true))
            {
                if (owner == null || owner == sourceComponent)
                    continue;

                var serializedObject = new SerializedObject(owner);
                serializedObject.UpdateIfRequiredOrScript();
                var property = serializedObject.GetIterator();
                bool changed = false;

                while (property.Next(true))
                {
                    if (property.propertyType == SerializedPropertyType.ObjectReference &&
                        property.objectReferenceValue == sourceComponent)
                    {
                        property.objectReferenceValue = movedComponent;
                        remappedReferenceCount++;
                        changed = true;
                    }
                    else if (property.propertyType == SerializedPropertyType.ExposedReference &&
                             property.exposedReferenceValue == sourceComponent)
                    {
                        property.exposedReferenceValue = movedComponent;
                        remappedReferenceCount++;
                        changed = true;
                    }
                }

                if (changed)
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }

            return remappedReferenceCount;
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
                    if (!TryResolveAssetReference(prop, referenceAssetPath, out targetRef, out string error))
                        return new { error };

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
                SavePrefabAssetNormalized(root, assetPath, BuildExplicitYamlPropertyRoots(propertyName));

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

            bool saved = false;
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

                if (SavePrefabAssetNormalized(root, assetPath) == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{assetPath}'.");
                saved = true;

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
                if (!saved)
                    RestoreAssetSnapshot(beforeSnapshot);
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

            bool saved = false;
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

                if (SavePrefabAssetNormalized(root, assetPath) == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{assetPath}'.");
                saved = true;

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
                if (!saved)
                    RestoreAssetSnapshot(beforeSnapshot);
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
                SavePrefabAssetNormalized(root, assetPath);

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

            bool saved = false;
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

                if (SavePrefabAssetNormalized(root, assetPath) == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{assetPath}'.");
                saved = true;

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
                if (!saved)
                    RestoreAssetSnapshot(beforeSnapshot);
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
                SavePrefabAssetNormalized(targetRoot, targetAssetPath);

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

        public static object CleanupMissingVariantOverrides(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefabAsset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };
            if (PrefabUtility.GetPrefabAssetType(prefabAsset) != PrefabAssetType.Variant)
                return new { error = $"Prefab at '{assetPath}' is not a Prefab Variant" };

            var beforeSnapshot = CaptureAssetText(assetPath);
            var modifications = PrefabUtility.GetPropertyModifications(prefabAsset) ??
                                Array.Empty<PropertyModification>();
            var kept = new List<PropertyModification>();
            var removed = new List<Dictionary<string, object>>();

            foreach (var modification in modifications)
            {
                string reason = null;
                if (modification.target == null)
                {
                    reason = "missing-target";
                }
                else if (string.IsNullOrEmpty(modification.propertyPath))
                {
                    reason = "missing-property-path";
                }
                else
                {
                    try
                    {
                        var serializedTarget = new SerializedObject(modification.target);
                        if (serializedTarget.FindProperty(modification.propertyPath) == null)
                            reason = "missing-serialized-field";
                    }
                    catch (Exception ex)
                    {
                        reason = $"unreadable-target: {ex.Message}";
                    }
                }

                if (reason == null)
                {
                    kept.Add(modification);
                    continue;
                }

                removed.Add(new Dictionary<string, object>
                {
                    { "target", modification.target == null ? "" : modification.target.name },
                    { "targetType", modification.target == null ? "" : modification.target.GetType().FullName },
                    { "propertyPath", modification.propertyPath ?? "" },
                    { "value", modification.value ?? "" },
                    { "reason", reason }
                });
            }

            bool dryRun = GetBool(args, "dryRun", false);
            if (!dryRun && removed.Count > 0)
            {
                PrefabUtility.SetPropertyModifications(prefabAsset, kept.ToArray());
                EditorUtility.SetDirty(prefabAsset);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "assetPath", assetPath },
                { "dryRun", dryRun },
                { "beforeCount", modifications.Length },
                { "keptCount", kept.Count },
                { "removedCount", removed.Count },
                { "removed", removed }
            };
            if (!dryRun)
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
            return result;
        }

        /// <summary>
        /// Apply multiple prefab asset edits in one load/save transaction.
        /// If any operation fails, no prefab asset save is performed.
        /// </summary>
        private static object ApplyTransactionImmediate(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var operations = GetDictionaryList(args, "operations");
            if (operations.Count == 0)
                return new { error = "operations must contain at least one operation" };

            var beforeSnapshot = CaptureAssetText(assetPath);
            GameObject root;
            try
            {
                root = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to load prefab at '{assetPath}': {ex.Message}" };
            }

            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            bool saved = false;
            try
            {
                var summaries = new List<Dictionary<string, object>>();
                for (int i = 0; i < operations.Count; i++)
                {
                    if (!TryApplyBatchOperation(root, operations[i], i, out var summary, out string error))
                    {
                        return new Dictionary<string, object>
                        {
                            { "error", error },
                            { "success", false },
                            { "saved", false },
                            { "failedOperationIndex", i },
                            { "failedOperation", operations[i] },
                            { "appliedOperationCount", summaries.Count },
                            { "operationSummaries", summaries },
                        };
                    }

                    summaries.Add(summary);
                }

                if (SavePrefabAssetNormalized(root, assetPath,
                        CollectExplicitYamlPropertyRoots(operations)) == null)
                    throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{assetPath}'.");
                saved = true;

                var result = new Dictionary<string, object>
                {
                    { "success", true },
                    { "saved", true },
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "operationCount", operations.Count },
                    { "operationSummaries", summaries },
                };
                AddPrefabFileDiff(result, beforeSnapshot, assetPath, args);
                return result;
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to edit prefab transaction: {ex.Message}", stackTrace = ex.StackTrace };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
                if (!saved)
                    RestoreAssetSnapshot(beforeSnapshot);
            }
        }

        public static object TransactionEdit(Dictionary<string, object> args)
        {
            args = PrepareTransactionEditArguments(args);
            if (!MCPExecutionOptions.TryParse(args, out var execution, out string executionError))
                return new { success = false, error = executionError };
            if (execution.ContinueOnError)
                return new { success = false, error = "execution.continueOnError is not supported by atomic prefab transactions" };
            var result = AddTransactionEditMetadata(ApplyTransactionImmediate(args), args);
            AddExecutionMetadata(result, execution, GetDictionaryList(args, "operations").Count);
            return result;
        }

        public static void TransactionEditDeferred(Dictionary<string, object> args, Action<object> resolve,
            Action<object> progress)
        {
            args = PrepareTransactionEditArguments(args);
            if (!MCPExecutionOptions.TryParse(args, out var execution, out string executionError))
            {
                resolve(new { success = false, error = executionError });
                return;
            }
            if (execution.ContinueOnError)
            {
                resolve(new { success = false, error = "execution.continueOnError is not supported by atomic prefab transactions" });
                return;
            }
            BatchEditDeferred(args, execution, result =>
            {
                result = AddTransactionEditMetadata(result, args);
                AddExecutionMetadata(result, execution, GetDictionaryList(args, "operations").Count);
                resolve(result);
            }, progress);
        }

        private static void AddExecutionMetadata(object result, MCPExecutionOptions execution, int operationCount)
        {
            if (result is Dictionary<string, object> dictionary)
                dictionary["execution"] = execution.ToResult(operationCount);
        }

        private static Dictionary<string, object> PrepareTransactionEditArguments(Dictionary<string, object> args)
        {
            if (args == null)
                args = new Dictionary<string, object>();

            if (args.ContainsKey("includePrefabFileDiff") == false)
                args["includePrefabFileDiff"] = true;
            if (args.ContainsKey("prefabFileDiffMode") == false)
                args["prefabFileDiffMode"] = "summary";
            if (args.ContainsKey("prefabFileDiffContextLines") == false)
                args["prefabFileDiffContextLines"] = 0;

            return args;
        }

        private static object AddTransactionEditMetadata(object batchResult, Dictionary<string, object> args)
        {
            var result = batchResult as Dictionary<string, object>;
            if (result == null)
                return batchResult;

            result["transaction"] = new Dictionary<string, object>
            {
                { "assetPath", GetString(args, "assetPath") },
                { "operationCount", GetDictionaryList(args, "operations").Count },
                { "diffMode", GetString(args, "prefabFileDiffMode") },
                { "saved", result.TryGetValue("saved", out var saved) && saved is bool savedValue && savedValue },
            };

            return result;
        }

        private static void BatchEditDeferred(Dictionary<string, object> args, MCPExecutionOptions execution,
            Action<object> resolve,
            Action<object> progress)
        {
            var componentTypes = CollectBatchEditComponentTypes(args);
            bool refreshAssets = GetBool(args, "refreshAssets", true);
            if (GetBool(args, "waitForTypes", true) == false || componentTypes.Count == 0)
            {
                StartBatchEditDeferred(args, execution, resolve, progress);
                return;
            }

            int timeoutMs = Math.Max(1, GetInt(args, "typeResolveTimeoutMs", 30000));
            int stableMs = Math.Max(0, GetInt(args, "typeResolveStableMs", 500));
            double startTime = EditorApplication.timeSinceStartup;
            double stableStartTime = -1;

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
                    bool editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                    var missingTypes = componentTypes
                        .Where(componentType => MCPComponentCommands.FindType(componentType) == null)
                        .ToList();

                    if (refreshAssets && missingTypes.Count > 0 && editorBusy == false)
                    {
                        complete(BuildAssetRefreshScheduledResult(componentTypes, missingTypes));
                        ScheduleAssetRefreshAfterResponse(args);
                        return;
                    }

                    if (refreshAssets && editorBusy)
                    {
                        complete(new Dictionary<string, object>
                        {
                            { "success", false },
                            { "error", "Unity is compiling or importing assets. Retry the prefab edit after the Editor reconnects and becomes idle." },
                            { "message", "Unity is compiling or importing assets. Retry the prefab edit after the Editor reconnects and becomes idle." },
                            { "errorCode", "editor_busy_before_prefab_edit" },
                            { "retryable", true },
                            { "typeResolution", new Dictionary<string, object>
                                {
                                    { "componentTypes", componentTypes },
                                    { "missingTypes", missingTypes },
                                    { "isCompiling", EditorApplication.isCompiling },
                                    { "isUpdating", EditorApplication.isUpdating },
                                    { "refreshedAssets", false },
                                }
                            },
                        });
                        return;
                    }

                    if (missingTypes.Count == 0 && editorBusy == false)
                    {
                        if (stableStartTime < 0)
                            stableStartTime = EditorApplication.timeSinceStartup;

                        double stableElapsedMs = (EditorApplication.timeSinceStartup - stableStartTime) * 1000d;
                        if (stableElapsedMs >= stableMs)
                        {
                            if (tick != null)
                                EditorApplication.update -= tick;
                            StartBatchEditDeferred(args, execution, resolve, progress);
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
                            { "error", $"Component types not found after waiting {timeoutMs} ms" },
                            { "typeResolution", new Dictionary<string, object>
                                {
                                    { "componentTypes", componentTypes },
                                    { "missingTypes", missingTypes },
                                    { "elapsedMs", (int)elapsedMs },
                                    { "timeoutMs", timeoutMs },
                                    { "refreshedAssets", false },
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
                    complete(new { error = $"Failed while waiting for component types: {ex.Message}", stackTrace = ex.StackTrace });
                }
            };

            EditorApplication.update += tick;
            tick();
        }

        private static Dictionary<string, object> BuildAssetRefreshScheduledResult(List<string> componentTypes,
            List<string> missingTypes)
        {
            const string message = "Referenced component types are not loaded yet. An asset refresh was scheduled after this response; retry the prefab edit after Unity reconnects and becomes idle.";
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", message },
                { "message", message },
                { "errorCode", "asset_refresh_scheduled" },
                { "retryable", true },
                { "typeResolution", new Dictionary<string, object>
                    {
                        { "componentTypes", componentTypes },
                        { "missingTypes", missingTypes },
                        { "refreshScheduled", true },
                        { "refreshedAssets", false },
                    }
                },
            };
        }

        private static bool _assetRefreshScheduled;

        private static void ScheduleAssetRefreshAfterResponse(Dictionary<string, object> args)
        {
            if (_assetRefreshScheduled)
                return;

            _assetRefreshScheduled = true;
            double refreshAfter = EditorApplication.timeSinceStartup + 0.25d;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup < refreshAfter)
                    return;

                EditorApplication.update -= tick;
                _assetRefreshScheduled = false;
                try
                {
                    RefreshAssetDatabase(args);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Unity MCP] Scheduled asset refresh failed: {ex.Message}");
                }
            };
            EditorApplication.update += tick;
        }

        private sealed class BatchEditDeferredState
        {
            public string AssetPath;
            public List<Dictionary<string, object>> Operations;
            public AssetTextSnapshot BeforeSnapshot;
            public GameObject Root;
            public readonly List<Dictionary<string, object>> Summaries = new List<Dictionary<string, object>>();
            public int NextOperationIndex;
            public double StartedAt;
            public int TimeoutMs;
            public int OperationsPerFrame;
            public int FrameBudgetMs;
            public MCPExecutionOptions Execution;
            public bool SaveAttempted;
            public bool Saved;
        }

        private static void StartBatchEditDeferred(Dictionary<string, object> args, MCPExecutionOptions execution,
            Action<object> resolve,
            Action<object> progress)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
            {
                resolve(new Dictionary<string, object>
                {
                    { "error", "assetPath is required" },
                    { "success", false },
                    { "saved", false },
                    { "saveAttempted", false },
                    { "partialPersisted", false },
                    { "partialPersistedKnown", true },
                    { "persistedState", "none" },
                });
                return;
            }

            var operations = GetDictionaryList(args, "operations");
            if (operations.Count == 0)
            {
                resolve(new Dictionary<string, object>
                {
                    { "error", "operations must contain at least one operation" },
                    { "success", false },
                    { "saved", false },
                    { "saveAttempted", false },
                    { "partialPersisted", false },
                    { "partialPersistedKnown", true },
                    { "persistedState", "none" },
                });
                return;
            }

            bool runBatched = execution.ResolveMode(operations.Count) == MCPExecutionMode.Batched;
            var state = new BatchEditDeferredState
            {
                AssetPath = assetPath,
                Operations = operations,
                BeforeSnapshot = CaptureAssetText(assetPath),
                StartedAt = EditorApplication.timeSinceStartup,
                TimeoutMs = execution.TimeoutMs,
                OperationsPerFrame = runBatched ? execution.OperationsPerFrame : int.MaxValue,
                FrameBudgetMs = runBatched ? execution.FrameBudgetMs : int.MaxValue,
                Execution = execution,
            };

            try
            {
                state.Root = PrefabUtility.LoadPrefabContents(assetPath);
            }
            catch (Exception ex)
            {
                resolve(new Dictionary<string, object>
                {
                    { "error", $"Failed to load prefab at '{assetPath}': {ex.Message}" },
                    { "success", false },
                    { "saved", false },
                    { "saveAttempted", false },
                    { "partialPersisted", false },
                    { "partialPersistedKnown", true },
                    { "persistedState", "none" },
                });
                return;
            }

            if (state.Root == null)
            {
                resolve(new Dictionary<string, object>
                {
                    { "error", $"Failed to load prefab at '{assetPath}'" },
                    { "success", false },
                    { "saved", false },
                    { "saveAttempted", false },
                    { "partialPersisted", false },
                    { "partialPersistedKnown", true },
                    { "persistedState", "none" },
                });
                return;
            }

            EditorApplication.CallbackFunction tick = null;
            Action<object> complete = result =>
            {
                if (tick != null)
                    EditorApplication.update -= tick;

                if (state.Root != null)
                {
                    PrefabUtility.UnloadPrefabContents(state.Root);
                    state.Root = null;
                }

                resolve(result);
            };

            tick = () =>
            {
                try
                {
                    double now = EditorApplication.timeSinceStartup;
                    int elapsedMs = (int)((now - state.StartedAt) * 1000d);
                    if (elapsedMs >= state.TimeoutMs)
                    {
                        complete(BuildBatchEditFailure(state, args,
                            $"Prefab transaction timed out after {state.TimeoutMs} ms before saving.",
                            "transaction_edit_timeout", true, elapsedMs, state.NextOperationIndex));
                        return;
                    }

                    double frameStartedAt = EditorApplication.timeSinceStartup;
                    int processedThisFrame = 0;
                    while (state.NextOperationIndex < state.Operations.Count)
                    {
                        int operationIndex = state.NextOperationIndex;
                        if (!TryApplyBatchOperation(state.Root, state.Operations[operationIndex], operationIndex,
                                out var summary, out string error))
                        {
                            complete(BuildBatchEditFailure(state, args, error, "batch_operation_failed",
                                false, elapsedMs, operationIndex, state.Operations[operationIndex]));
                            return;
                        }

                        state.Summaries.Add(summary);
                        state.NextOperationIndex++;
                        processedThisFrame++;

                        progress?.Invoke(BuildBatchEditProgress(state, elapsedMs, "applying"));

                        double frameElapsedMs = (EditorApplication.timeSinceStartup - frameStartedAt) * 1000d;
                        if (processedThisFrame >= state.OperationsPerFrame ||
                            frameElapsedMs >= state.FrameBudgetMs)
                        {
                            return;
                        }

                        elapsedMs = (int)((EditorApplication.timeSinceStartup - state.StartedAt) * 1000d);
                        if (elapsedMs >= state.TimeoutMs)
                            break;
                    }

                    if (state.NextOperationIndex < state.Operations.Count)
                        return;

                    state.SaveAttempted = true;
                    progress?.Invoke(BuildBatchEditProgress(state, elapsedMs, "saving"));
                    var savedRoot = SavePrefabAssetNormalized(state.Root, state.AssetPath,
                        CollectExplicitYamlPropertyRoots(state.Operations));
                    if (savedRoot == null)
                        throw new InvalidOperationException($"SaveAsPrefabAsset returned null for '{state.AssetPath}'.");
                    state.Saved = true;

                    var result = new Dictionary<string, object>
                    {
                        { "success", true },
                        { "saved", true },
                        { "saveAttempted", true },
                        { "partialPersisted", false },
                        { "partialPersistedKnown", true },
                        { "persistedState", "complete" },
                        { "prefab", state.Root.name },
                        { "assetPath", state.AssetPath },
                        { "operationCount", state.Operations.Count },
                        { "appliedOperationCount", state.Summaries.Count },
                        { "operationSummaries", state.Summaries },
                        { "elapsedMs", (int)((EditorApplication.timeSinceStartup - state.StartedAt) * 1000d) },
                        { "execution", state.Execution.ToResult(state.Operations.Count) },
                    };
                    AddPrefabFileDiff(result, state.BeforeSnapshot, state.AssetPath, args);
                    complete(result);
                }
                catch (Exception ex)
                {
                    int elapsedMs = (int)((EditorApplication.timeSinceStartup - state.StartedAt) * 1000d);
                    complete(BuildBatchEditFailure(state, args,
                        $"Failed to edit prefab transaction: {ex.Message}", "transaction_edit_exception",
                        false, elapsedMs, state.NextOperationIndex, null, ex.StackTrace));
                }
            };

            progress?.Invoke(BuildBatchEditProgress(state, 0, "started"));
            EditorApplication.update += tick;
            tick();
        }

        private static Dictionary<string, object> BuildBatchEditProgress(BatchEditDeferredState state,
            int elapsedMs, string phase)
        {
            var saveAttemptedWithoutSuccess = state.SaveAttempted && state.Saved == false;
            return new Dictionary<string, object>
            {
                { "phase", phase },
                { "assetPath", state.AssetPath },
                { "operationCount", state.Operations.Count },
                { "appliedOperationCount", state.Summaries.Count },
                { "nextOperationIndex", state.NextOperationIndex },
                { "elapsedMs", elapsedMs },
                { "timeoutMs", state.TimeoutMs },
                { "saveAttempted", state.SaveAttempted },
                { "saved", state.Saved },
                { "partialPersisted", saveAttemptedWithoutSuccess ? (object)null : false },
                { "partialPersistedKnown", saveAttemptedWithoutSuccess == false },
                { "persistedState", state.Saved ? "complete" : saveAttemptedWithoutSuccess ? "unknown" : "none" },
                { "execution", state.Execution.ToResult(state.Operations.Count) },
            };
        }

        private static Dictionary<string, object> BuildBatchEditFailure(BatchEditDeferredState state,
            Dictionary<string, object> args, string error, string errorCode, bool retryable, int elapsedMs,
            int failedOperationIndex, Dictionary<string, object> failedOperation = null, string stackTrace = null)
        {
            var saveAttemptedWithoutSuccess = state.SaveAttempted && state.Saved == false;
            var result = new Dictionary<string, object>
            {
                { "error", error },
                { "message", error },
                { "errorCode", errorCode },
                { "retryable", retryable },
                { "success", false },
                { "timedOut", errorCode == "transaction_edit_timeout" },
                { "saved", state.Saved },
                { "saveAttempted", state.SaveAttempted },
                { "partialPersisted", saveAttemptedWithoutSuccess ? (object)null : false },
                { "partialPersistedKnown", saveAttemptedWithoutSuccess == false },
                { "persistedState", state.Saved ? "complete" : saveAttemptedWithoutSuccess ? "unknown" : "none" },
                { "assetPath", state.AssetPath },
                { "operationCount", state.Operations.Count },
                { "appliedOperationCount", state.Summaries.Count },
                { "nextOperationIndex", state.NextOperationIndex },
                { "failedOperationIndex", failedOperationIndex },
                { "operationSummaries", state.Summaries },
                { "elapsedMs", elapsedMs },
                { "timeoutMs", state.TimeoutMs },
                { "execution", state.Execution.ToResult(state.Operations.Count) },
            };

            if (failedOperation != null)
                result["failedOperation"] = failedOperation;
            if (string.IsNullOrEmpty(stackTrace) == false)
                result["stackTrace"] = stackTrace;

            return result;
        }

        // ─── Helpers ───

        private static void RefreshAssetsThen(Dictionary<string, object> args, Action<object> resolve,
            Func<object> action)
        {
            RefreshAssetsThenDeferred(args, resolve, () => resolve(action()));
        }

        private static void RefreshAssetsThenDeferred(Dictionary<string, object> args, Action<object> resolve,
            Action action)
        {
            int timeoutMs = Math.Max(1, GetInt(args, "assetRefreshTimeoutMs", GetInt(args, "typeResolveTimeoutMs", 30000)));
            int stableMs = Math.Max(0, GetInt(args, "assetRefreshStableMs", GetInt(args, "typeResolveStableMs", 500)));
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
                    if (refreshRequested == false)
                    {
                        refreshRequested = true;
                        RefreshAssetDatabase(args);
                    }

                    bool editorBusy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                    if (editorBusy == false)
                    {
                        if (stableStartTime < 0)
                            stableStartTime = EditorApplication.timeSinceStartup;

                        double stableElapsedMs = (EditorApplication.timeSinceStartup - stableStartTime) * 1000d;
                        if (stableElapsedMs >= stableMs)
                        {
                            if (tick != null)
                                EditorApplication.update -= tick;
                            action();
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
                            { "error", $"Asset refresh did not finish after waiting {timeoutMs} ms" },
                            { "assetRefresh", new Dictionary<string, object>
                                {
                                    { "elapsedMs", (int)elapsedMs },
                                    { "timeoutMs", timeoutMs },
                                    { "isCompiling", EditorApplication.isCompiling },
                                    { "isUpdating", EditorApplication.isUpdating },
                                }
                            },
                        });
                    }
                }
                catch (Exception ex)
                {
                    complete(new { error = $"Failed while waiting for asset refresh: {ex.Message}", stackTrace = ex.StackTrace });
                }
            };

            EditorApplication.update += tick;
            tick();
        }

        private static void RefreshAssetDatabase(Dictionary<string, object> args)
        {
            bool forceUpdate = GetBool(args, "forceAssetRefreshUpdate", GetBool(args, "forceUpdate", true));
            var options = forceUpdate ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default;
            AssetDatabase.Refresh(options);
        }

        private static GameObject SavePrefabAssetNormalized(GameObject root, string assetPath,
            ISet<string> explicitYamlPropertyRoots = null)
        {
            string absolutePath = GetAbsoluteAssetPath(assetPath);
            byte[] beforeBytes = !string.IsNullOrEmpty(absolutePath) && File.Exists(absolutePath)
                ? File.ReadAllBytes(absolutePath)
                : null;
            var savedRoot = PrefabUtility.SaveAsPrefabAsset(root, assetPath);
            if (savedRoot != null)
                StabilizePrefabYaml(assetPath, beforeBytes, explicitYamlPropertyRoots);
            return savedRoot;
        }

        private static void StabilizePrefabYaml(string assetPath, byte[] beforeBytes,
            ISet<string> explicitYamlPropertyRoots)
        {
            string absolutePath = GetAbsoluteAssetPath(assetPath);
            if (string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath))
                return;

            byte[] afterBytes = File.ReadAllBytes(absolutePath);
            bool hasUtf8Bom = HasUtf8Bom(beforeBytes) || beforeBytes == null && HasUtf8Bom(afterBytes);
            string afterText = DecodeUtf8(afterBytes);
            string normalized = NormalizeYamlWhitespace(afterText);

            if (beforeBytes != null)
            {
                string beforeText = DecodeUtf8(beforeBytes);
                if (TryPreserveYamlBlockOrder(beforeText, normalized, explicitYamlPropertyRoots,
                        out string reordered))
                    normalized = reordered;
            }

            string currentText = DecodeUtf8(afterBytes);
            if (normalized == currentText && hasUtf8Bom == HasUtf8Bom(afterBytes))
                return;

            File.WriteAllText(absolutePath, normalized, new UTF8Encoding(hasUtf8Bom));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }

        private static string NormalizeYamlWhitespace(string text)
        {
            return Regex.Replace(text ?? "", @"[\t ]+(?=\r?$)", "", RegexOptions.Multiline);
        }

        private static bool TryPreserveYamlBlockOrder(string beforeText, string afterText,
            ISet<string> explicitYamlPropertyRoots, out string result)
        {
            result = afterText;
            if (!TryParseYamlFile(beforeText, out var beforeFile) ||
                !TryParseYamlFile(afterText, out var afterFile))
                return false;

            var afterByKey = new Dictionary<string, YamlObjectBlock>(StringComparer.Ordinal);
            foreach (var block in afterFile.Blocks)
            {
                if (afterByKey.ContainsKey(block.Key))
                    return false;
                afterByKey.Add(block.Key, block);
            }

            var ordered = new List<YamlObjectBlock>();
            var included = new HashSet<string>(StringComparer.Ordinal);
            bool preservedUnrelatedFields = false;
            foreach (var beforeBlock in beforeFile.Blocks)
            {
                if (!afterByKey.TryGetValue(beforeBlock.Key, out var afterBlock))
                    continue;

                string beforeCanonical = CanonicalYamlText(beforeBlock.Text);
                string afterCanonical = CanonicalYamlText(afterBlock.Text);
                if (beforeCanonical == afterCanonical)
                {
                    ordered.Add(beforeBlock);
                }
                else if (ShouldPreserveUnrelatedAddedFields(beforeBlock, afterBlock,
                             explicitYamlPropertyRoots))
                {
                    ordered.Add(beforeBlock);
                    preservedUnrelatedFields = true;
                }
                else
                {
                    ordered.Add(afterBlock);
                }
                included.Add(beforeBlock.Key);
            }

            foreach (var afterBlock in afterFile.Blocks)
            {
                if (included.Add(afterBlock.Key))
                    ordered.Add(afterBlock);
            }

            if (ordered.Count != afterFile.Blocks.Count)
                return false;

            string preamble = CanonicalYamlText(beforeFile.Preamble) ==
                              CanonicalYamlText(afterFile.Preamble)
                ? beforeFile.Preamble
                : NormalizeYamlWhitespace(afterFile.Preamble);
            string candidate = preamble + string.Concat(ordered.Select(block => block.Text));
            candidate = ApplyLineEnding(candidate, beforeFile.LineEnding);

            if (!TryParseYamlFile(candidate, out var candidateFile))
                return false;
            if (preservedUnrelatedFields)
            {
                if (!YamlFilesHaveSameBlockKeys(candidateFile, afterFile))
                    return false;
            }
            else if (!YamlFilesHaveEquivalentBlocks(candidateFile, afterFile))
            {
                return false;
            }

            result = candidate;
            return true;
        }

        private static bool ShouldPreserveUnrelatedAddedFields(YamlObjectBlock beforeBlock,
            YamlObjectBlock afterBlock, ISet<string> explicitYamlPropertyRoots)
        {
            int separatorIndex = beforeBlock.Key.IndexOf(':');
            if (separatorIndex <= 0 ||
                !int.TryParse(beforeBlock.Key.Substring(0, separatorIndex), out int objectType) ||
                objectType == 1 || objectType == 4 || objectType == 224 || objectType == 1001)
                return false;

            string[] beforeLines = CanonicalYamlText(beforeBlock.Text).Split('\n');
            string[] afterLines = CanonicalYamlText(afterBlock.Text).Split('\n');
            var addedLines = new List<string>();
            int beforeIndex = 0;
            string currentPropertyRoot = "";
            foreach (string afterLine in afterLines)
            {
                string propertyRoot = GetTopLevelYamlPropertyName(afterLine);
                if (string.IsNullOrEmpty(propertyRoot) == false)
                    currentPropertyRoot = propertyRoot;

                if (beforeIndex < beforeLines.Length && afterLine == beforeLines[beforeIndex])
                {
                    beforeIndex++;
                }
                else
                {
                    addedLines.Add(afterLine);
                    if (explicitYamlPropertyRoots != null &&
                        explicitYamlPropertyRoots.Contains(currentPropertyRoot))
                        return false;
                }
            }

            if (beforeIndex != beforeLines.Length || addedLines.Count == 0)
                return false;

            if (explicitYamlPropertyRoots == null || explicitYamlPropertyRoots.Count == 0)
                return true;

            foreach (string line in addedLines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    trimmed = trimmed.Substring(2).TrimStart();
                int colonIndex = trimmed.IndexOf(':');
                if (colonIndex <= 0)
                    continue;
                string propertyName = trimmed.Substring(0, colonIndex);
                if (explicitYamlPropertyRoots.Contains(propertyName))
                    return false;
            }

            return true;
        }

        private static string GetTopLevelYamlPropertyName(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length < 3 || line[0] != ' ' || line[1] != ' ' ||
                line[2] == ' ' || line[2] == '\t' || line[2] == '-')
                return "";

            int colonIndex = line.IndexOf(':', 2);
            if (colonIndex <= 2)
                return "";

            return line.Substring(2, colonIndex - 2);
        }

        private static bool YamlFilesHaveSameBlockKeys(YamlFile left, YamlFile right)
        {
            if (left.Blocks.Count != right.Blocks.Count)
                return false;
            return new HashSet<string>(left.Blocks.Select(block => block.Key), StringComparer.Ordinal)
                .SetEquals(right.Blocks.Select(block => block.Key));
        }

        private static HashSet<string> BuildExplicitYamlPropertyRoots(params string[] propertyPaths)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (propertyPaths == null)
                return roots;
            foreach (string propertyPath in propertyPaths)
            {
                if (string.IsNullOrWhiteSpace(propertyPath))
                    continue;
                int separatorIndex = propertyPath.IndexOf('.');
                roots.Add(separatorIndex > 0 ? propertyPath.Substring(0, separatorIndex) : propertyPath);
            }
            return roots;
        }

        private static HashSet<string> CollectExplicitYamlPropertyRoots(
            IEnumerable<Dictionary<string, object>> operations)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);
            if (operations == null)
                return roots;

            foreach (var operation in operations)
            {
                string operationType = GetOperationType(operation);
                if (operationType != "setproperty" && operationType != "setreference")
                    continue;

                var properties = GetDictionary(operation, "properties");
                if (properties != null)
                {
                    foreach (string propertyName in properties.Keys)
                        roots.UnionWith(BuildExplicitYamlPropertyRoots(propertyName));
                }
                else
                {
                    roots.UnionWith(BuildExplicitYamlPropertyRoots(GetString(operation, "propertyName")));
                }
            }

            return roots;
        }

        private static bool YamlFilesHaveEquivalentBlocks(YamlFile left, YamlFile right)
        {
            if (left.Blocks.Count != right.Blocks.Count)
                return false;

            var rightByKey = right.Blocks.ToDictionary(block => block.Key, block =>
                NormalizeYamlWhitespace(block.Text).Replace("\r\n", "\n"), StringComparer.Ordinal);
            foreach (var block in left.Blocks)
            {
                if (!rightByKey.TryGetValue(block.Key, out string rightText) ||
                    NormalizeYamlWhitespace(block.Text).Replace("\r\n", "\n") != rightText)
                    return false;
            }
            return true;
        }

        private static string CanonicalYamlText(string text)
        {
            return NormalizeYamlWhitespace(text).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static bool TryParseYamlFile(string text, out YamlFile file)
        {
            file = null;
            if (string.IsNullOrEmpty(text))
                return false;

            string lineEnding = text.Contains("\r\n") ? "\r\n" : "\n";
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var matches = Regex.Matches(normalized,
                @"(?m)^--- !u!(?<type>\d+) &(?<id>-?\d+)(?: stripped)?[\t ]*$");
            if (matches.Count == 0)
                return false;

            var blocks = new List<YamlObjectBlock>(matches.Count);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < matches.Count; index++)
            {
                Match match = matches[index];
                int end = index + 1 < matches.Count ? matches[index + 1].Index : normalized.Length;
                string key = match.Groups["type"].Value + ":" + match.Groups["id"].Value;
                if (!keys.Add(key))
                    return false;
                blocks.Add(new YamlObjectBlock(key, normalized.Substring(match.Index, end - match.Index)));
            }

            file = new YamlFile(normalized.Substring(0, matches[0].Index), blocks, lineEnding);
            return true;
        }

        private static string ApplyLineEnding(string text, string lineEnding)
        {
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return lineEnding == "\r\n" ? normalized.Replace("\n", "\r\n") : normalized;
        }

        private static string DecodeUtf8(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return "";
            int offset = HasUtf8Bom(bytes) ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private sealed class YamlFile
        {
            public readonly string Preamble;
            public readonly List<YamlObjectBlock> Blocks;
            public readonly string LineEnding;

            public YamlFile(string preamble, List<YamlObjectBlock> blocks, string lineEnding)
            {
                Preamble = preamble;
                Blocks = blocks;
                LineEnding = lineEnding;
            }
        }

        private sealed class YamlObjectBlock
        {
            public readonly string Key;
            public readonly string Text;

            public YamlObjectBlock(string key, string text)
            {
                Key = key;
                Text = text;
            }
        }

        private static bool RestoreAssetSnapshot(AssetTextSnapshot snapshot)
        {
            if (snapshot == null || !string.IsNullOrEmpty(snapshot.ReadError))
                return false;

            try
            {
                if (!snapshot.Exists)
                {
                    if (File.Exists(snapshot.AbsolutePath))
                    {
                        AssetDatabase.DeleteAsset(snapshot.AssetPath);
                        return true;
                    }
                    return false;
                }

                byte[] currentBytes = File.Exists(snapshot.AbsolutePath)
                    ? File.ReadAllBytes(snapshot.AbsolutePath)
                    : Array.Empty<byte>();
                if (snapshot.Bytes != null && currentBytes.SequenceEqual(snapshot.Bytes))
                    return false;

                File.WriteAllBytes(snapshot.AbsolutePath, snapshot.Bytes ?? Array.Empty<byte>());
                AssetDatabase.ImportAsset(snapshot.AssetPath, ImportAssetOptions.ForceUpdate);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Unity MCP] Failed to restore prefab asset '{snapshot.AssetPath}': {ex.Message}");
                return false;
            }
        }

        private static GameObject FindInPrefab(GameObject root, string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return root;

            string[] parts = prefabPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Transform current = root.transform;
            int startIndex = parts.Length > 0 && parts[0] == root.name ? 1 : 0;
            for (int partIndex = startIndex; partIndex < parts.Length; partIndex++)
            {
                Transform next = null;
                for (int childIndex = 0; childIndex < current.childCount; childIndex++)
                {
                    var child = current.GetChild(childIndex);
                    if (child.name == parts[partIndex])
                    {
                        next = child;
                        break;
                    }
                }

                if (next == null)
                    return null;
                current = next;
            }
            return current.gameObject;
        }

        private static int CountHierarchyNodes(GameObject go)
        {
            int count = 1;
            for (int i = 0; i < go.transform.childCount; i++)
                count += CountHierarchyNodes(go.transform.GetChild(i).gameObject);
            return count;
        }

        private static Dictionary<string, object> BuildHierarchyNode(GameObject go, int depth, int maxDepth,
            ref int nodeCount, int maxNodes)
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
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
            };
            MCPTransformSerialization.AddLocal(node, go.transform);

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    if (nodeCount >= maxNodes)
                        break;
                    var child = BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth,
                        ref nodeCount, maxNodes);
                    if (child != null)
                        children.Add(child);
                }
                if (children.Count > 0)
                    node["children"] = children;
                node["childCount"] = go.transform.childCount;
                if (children.Count < go.transform.childCount)
                {
                    node["childrenIncluded"] = children.Count;
                    node["childrenTruncated"] = true;
                }
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private static bool TryApplyBatchOperation(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            error = "";

            string operationType = GetOperationType(operation);
            if (string.IsNullOrEmpty(operationType))
            {
                error = $"Operation {operationIndex} is missing type/op/action";
                return false;
            }

            switch (operationType)
            {
                case "addcomponent":
                    return TryBatchAddComponent(root, operation, operationIndex, out summary, out error);
                case "setproperty":
                    return TryBatchSetProperty(root, operation, operationIndex, out summary, out error);
                case "setreference":
                    return TryBatchSetReference(root, operation, operationIndex, out summary, out error);
                case "arrayinsert":
                case "arrayremove":
                case "arrayset":
                case "arrayclear":
                    return TryBatchArrayOperation(root, operation, operationIndex, operationType,
                        out summary, out error);
                case "addgameobject":
                    return TryBatchAddGameObject(root, operation, operationIndex, out summary, out error);
                case "instantiateprefab":
                    return TryBatchInstantiatePrefab(root, operation, operationIndex, out summary, out error);
                case "removecomponent":
                    return TryBatchRemoveComponent(root, operation, operationIndex, out summary, out error);
                case "removegameobject":
                    return TryBatchRemoveGameObject(root, operation, operationIndex, out summary, out error);
                case "movegameobject":
                    return TryBatchMoveGameObject(root, operation, operationIndex, out summary, out error);
                default:
                    error = $"Unsupported prefab batch operation '{operationType}' at index {operationIndex}";
                    return false;
            }
        }

        private static bool TryBatchAddComponent(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            error = "";
            string prefabPath = GetString(operation, "prefabPath");
            string componentType = GetString(operation, "componentType");
            if (string.IsNullOrEmpty(componentType))
            {
                error = $"Operation {operationIndex}: componentType is required";
                return false;
            }

            var go = FindInPrefab(root, prefabPath);
            if (go == null)
            {
                error = $"Operation {operationIndex}: GameObject '{prefabPath}' not found in prefab";
                return false;
            }

            Type type = MCPComponentCommands.FindType(componentType);
            if (type == null)
            {
                error = $"Operation {operationIndex}: Type '{componentType}' not found";
                return false;
            }

            var component = go.AddComponent(type);
            var changedProperties = new List<string>();
            var properties = GetDictionary(operation, "properties");
            if (properties != null &&
                !TryApplySerializedProperties(component, properties, changedProperties, out error))
            {
                error = $"Operation {operationIndex}: {error}";
                return false;
            }

            summary = BuildBatchSummary(operationIndex, "addComponent", go, component);
            summary["prefabPath"] = GetPrefabPath(root, go);
            summary["properties"] = changedProperties;
            return true;
        }

        private static bool TryBatchSetProperty(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            if (!TryGetBatchComponent(root, operation, operationIndex, out var go, out var component, out error))
                return false;

            var changedProperties = new List<string>();
            var properties = GetDictionary(operation, "properties");
            if (properties != null)
            {
                if (!TryApplySerializedProperties(component, properties, changedProperties, out error))
                {
                    error = $"Operation {operationIndex}: {error}";
                    return false;
                }
            }
            else
            {
                string propertyName = GetString(operation, "propertyName");
                if (string.IsNullOrEmpty(propertyName))
                {
                    error = $"Operation {operationIndex}: propertyName or properties is required";
                    return false;
                }

                if (!operation.ContainsKey("value"))
                {
                    error = $"Operation {operationIndex}: value is required";
                    return false;
                }

                if (!TryApplySerializedProperty(component, propertyName, operation["value"], out error))
                {
                    error = $"Operation {operationIndex}: {error}";
                    return false;
                }

                changedProperties.Add(propertyName);
            }

            summary = BuildBatchSummary(operationIndex, "setProperty", go, component);
            summary["prefabPath"] = GetPrefabPath(root, go);
            summary["properties"] = changedProperties;
            return true;
        }

        private static bool TryBatchSetReference(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            error = "";
            string prefabPath = GetString(operation, "prefabPath");
            string propertyName = GetString(operation, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                error = $"Operation {operationIndex}: propertyName is required";
                return false;
            }

            var go = FindInPrefab(root, prefabPath);
            if (go == null)
            {
                error = $"Operation {operationIndex}: GameObject '{prefabPath}' not found in prefab";
                return false;
            }

            Component component = null;
            string componentType = GetString(operation, "componentType");
            if (string.IsNullOrEmpty(componentType) == false)
            {
                if (!TryGetBatchComponent(root, operation, operationIndex, out _, out component, out error))
                    return false;
            }
            else
            {
                foreach (var candidate in go.GetComponents<Component>())
                {
                    if (candidate == null)
                        continue;

                    var serializedCandidate = new SerializedObject(candidate);
                    if (serializedCandidate.FindProperty(propertyName) != null)
                    {
                        component = candidate;
                        break;
                    }
                }
            }

            if (component == null)
            {
                error = $"Operation {operationIndex}: Component '{componentType}' not found on '{go.name}', or no component has property '{propertyName}'";
                return false;
            }

            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propertyName);
            if (prop == null)
            {
                error = $"Operation {operationIndex}: Property '{propertyName}' not found";
                return false;
            }

            if (prop.propertyType != SerializedPropertyType.ObjectReference)
            {
                error = $"Operation {operationIndex}: Property '{propertyName}' is not an ObjectReference";
                return false;
            }

            if (!TryResolveBatchReference(root, prop, operation, out UnityEngine.Object targetRef,
                    out string refDescription, out error))
            {
                error = $"Operation {operationIndex}: {error}";
                return false;
            }

            prop.objectReferenceValue = targetRef;
            serialized.ApplyModifiedProperties();

            summary = BuildBatchSummary(operationIndex, "setReference", go, component);
            summary["prefabPath"] = GetPrefabPath(root, go);
            summary["property"] = propertyName;
            summary["reference"] = refDescription;
            return true;
        }

        private static bool TryBatchArrayOperation(GameObject root, Dictionary<string, object> operation,
            int operationIndex, string operationType, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            if (!TryGetBatchComponent(root, operation, operationIndex, out var go, out var component, out error))
                return false;

            string propertyName = GetString(operation, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
            {
                error = $"Operation {operationIndex}: propertyName is required";
                return false;
            }

            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                error = $"Operation {operationIndex}: Property '{propertyName}' not found";
                return false;
            }
            if (!property.isArray || property.propertyType == SerializedPropertyType.String)
            {
                error = $"Operation {operationIndex}: Property '{propertyName}' is not an array or list";
                return false;
            }

            int beforeSize = property.arraySize;
            int index = GetInt(operation, "index", -1);
            try
            {
                switch (operationType)
                {
                    case "arrayinsert":
                        if (index < 0 || index > property.arraySize)
                            throw new IndexOutOfRangeException($"Index {index} is invalid for size {property.arraySize}");
                        property.InsertArrayElementAtIndex(index);
                        if (operation.TryGetValue("value", out var insertValue))
                            MCPComponentCommands.SetSerializedValue(property.GetArrayElementAtIndex(index), insertValue);
                        break;
                    case "arrayremove":
                        if (index < 0 || index >= property.arraySize)
                            throw new IndexOutOfRangeException($"Index {index} is invalid for size {property.arraySize}");
                        int sizeBeforeDelete = property.arraySize;
                        property.DeleteArrayElementAtIndex(index);
                        if (property.arraySize == sizeBeforeDelete)
                            property.DeleteArrayElementAtIndex(index);
                        break;
                    case "arrayset":
                        if (index < 0 || index >= property.arraySize)
                            throw new IndexOutOfRangeException($"Index {index} is invalid for size {property.arraySize}");
                        if (!operation.TryGetValue("value", out var setValue))
                            throw new ArgumentException("value is required");
                        MCPComponentCommands.SetSerializedValue(property.GetArrayElementAtIndex(index), setValue);
                        break;
                    case "arrayclear":
                        property.ClearArray();
                        break;
                }

                serialized.ApplyModifiedProperties();
            }
            catch (Exception ex)
            {
                error = $"Operation {operationIndex}: {ex.Message}";
                return false;
            }

            summary = BuildBatchSummary(operationIndex, operationType, go, component);
            summary["prefabPath"] = GetPrefabPath(root, go);
            summary["property"] = propertyName;
            summary["index"] = index;
            summary["beforeSize"] = beforeSize;
            summary["afterSize"] = property.arraySize;
            error = "";
            return true;
        }

        private static bool TryBatchAddGameObject(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            error = "";
            string parentPrefabPath = GetString(operation, "parentPrefabPath");
            string name = GetString(operation, "name");
            if (string.IsNullOrEmpty(name))
            {
                error = $"Operation {operationIndex}: name is required";
                return false;
            }

            var parent = FindInPrefab(root, parentPrefabPath);
            if (parent == null)
            {
                error = $"Operation {operationIndex}: Parent '{parentPrefabPath}' not found in prefab";
                return false;
            }

            string primitiveType = GetString(operation, "primitiveType");
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
            ApplyTransformArguments(newGo.transform, operation);

            summary = BuildBatchSummary(operationIndex, "addGameObject", newGo, null);
            summary["parent"] = string.IsNullOrEmpty(parentPrefabPath) ? "root" : parentPrefabPath;
            summary["prefabPath"] = GetPrefabPath(root, newGo);
            return true;
        }

        private static bool TryBatchInstantiatePrefab(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            string sourcePrefabPath = GetString(operation, "sourcePrefabPath");
            if (string.IsNullOrEmpty(sourcePrefabPath))
            {
                error = $"Operation {operationIndex}: sourcePrefabPath is required";
                return false;
            }

            string parentPrefabPath = GetString(operation, "parentPrefabPath");
            var parent = FindInPrefab(root, parentPrefabPath);
            if (parent == null)
            {
                error = $"Operation {operationIndex}: Parent '{parentPrefabPath}' not found in prefab";
                return false;
            }

            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (sourcePrefab == null)
            {
                error = $"Operation {operationIndex}: Source prefab not found at '{sourcePrefabPath}'";
                return false;
            }

            var instance = PrefabUtility.InstantiatePrefab(sourcePrefab, root.scene) as GameObject;
            if (instance == null)
            {
                error = $"Operation {operationIndex}: Failed to instantiate prefab '{sourcePrefabPath}'";
                return false;
            }

            instance.transform.SetParent(parent.transform, false);

            string name = GetString(operation, "name");
            if (string.IsNullOrEmpty(name) == false)
                instance.name = name;

            ApplyTransformArguments(instance.transform, operation);

            int siblingIndex = GetInt(operation, "siblingIndex", -1);
            if (siblingIndex >= 0)
                instance.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, parent.transform.childCount - 1));

            summary = BuildBatchSummary(operationIndex, "instantiatePrefab", instance, null);
            summary["sourcePrefabPath"] = sourcePrefabPath;
            summary["parent"] = string.IsNullOrEmpty(parentPrefabPath) ? "root" : parentPrefabPath;
            summary["prefabPath"] = GetPrefabPath(root, instance);
            summary["siblingIndex"] = instance.transform.GetSiblingIndex();
            error = "";
            return true;
        }

        private static bool TryBatchRemoveComponent(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            if (!TryGetBatchComponent(root, operation, operationIndex, out var go, out var component, out error))
                return false;

            string componentName = component.GetType().Name;
            int index = GetInt(operation, "index", GetInt(operation, "componentIndex", 0));
            UnityEngine.Object.DestroyImmediate(component);

            summary = new Dictionary<string, object>
            {
                { "index", operationIndex },
                { "type", "removeComponent" },
                { "gameObject", go.name },
                { "prefabPath", GetPrefabPath(root, go) },
                { "component", componentName },
                { "componentIndex", index },
            };
            return true;
        }

        private static bool TryBatchRemoveGameObject(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            error = "";
            string prefabPath = GetString(operation, "prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
            {
                error = $"Operation {operationIndex}: prefabPath is required";
                return false;
            }

            var go = FindInPrefab(root, prefabPath);
            if (go == null)
            {
                error = $"Operation {operationIndex}: GameObject '{prefabPath}' not found in prefab";
                return false;
            }

            if (go == root)
            {
                error = $"Operation {operationIndex}: Cannot delete the root GameObject of a prefab";
                return false;
            }

            string deletedName = go.name;
            UnityEngine.Object.DestroyImmediate(go);

            summary = new Dictionary<string, object>
            {
                { "index", operationIndex },
                { "type", "removeGameObject" },
                { "deletedGameObject", deletedName },
                { "prefabPath", prefabPath },
            };
            return true;
        }

        private static bool TryBatchMoveGameObject(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out Dictionary<string, object> summary, out string error)
        {
            summary = null;
            string prefabPath = GetString(operation, "prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
            {
                error = $"Operation {operationIndex}: prefabPath is required";
                return false;
            }

            var go = FindInPrefab(root, prefabPath);
            if (go == null)
            {
                error = $"Operation {operationIndex}: GameObject '{prefabPath}' not found in prefab";
                return false;
            }

            if (go == root)
            {
                error = $"Operation {operationIndex}: Cannot move the root GameObject of a prefab";
                return false;
            }

            string newParentPrefabPath = GetString(operation, "newParentPrefabPath");
            var newParent = FindInPrefab(root, newParentPrefabPath);
            if (newParent == null)
            {
                error = $"Operation {operationIndex}: New parent '{newParentPrefabPath}' not found in prefab";
                return false;
            }

            string oldPath = GetPrefabPath(root, go);
            string oldParentPath = go.transform.parent != null ? GetPrefabPath(root, go.transform.parent.gameObject) : "";
            int oldSiblingIndex = go.transform.GetSiblingIndex();
            bool worldPositionStays = GetBool(operation, "worldPositionStays", false);
            int siblingIndex = GetInt(operation, "siblingIndex", -1);

            go.transform.SetParent(newParent.transform, worldPositionStays);
            if (siblingIndex >= 0)
                go.transform.SetSiblingIndex(Mathf.Clamp(siblingIndex, 0, newParent.transform.childCount - 1));

            summary = new Dictionary<string, object>
            {
                { "index", operationIndex },
                { "type", "moveGameObject" },
                { "gameObject", go.name },
                { "oldPath", oldPath },
                { "newPath", GetPrefabPath(root, go) },
                { "oldParent", oldParentPath },
                { "newParent", string.IsNullOrEmpty(newParentPrefabPath) ? "root" : newParentPrefabPath },
                { "oldSiblingIndex", oldSiblingIndex },
                { "newSiblingIndex", go.transform.GetSiblingIndex() },
            };
            error = "";
            return true;
        }

        private static bool TryGetBatchComponent(GameObject root, Dictionary<string, object> operation,
            int operationIndex, out GameObject go, out Component component, out string error)
        {
            go = null;
            component = null;
            error = "";

            string prefabPath = GetString(operation, "prefabPath");
            string componentType = GetString(operation, "componentType");
            if (string.IsNullOrEmpty(componentType))
            {
                error = $"Operation {operationIndex}: componentType is required";
                return false;
            }

            go = FindInPrefab(root, prefabPath);
            if (go == null)
            {
                error = $"Operation {operationIndex}: GameObject '{prefabPath}' not found in prefab";
                return false;
            }

            Type type = MCPComponentCommands.FindType(componentType);
            if (type == null)
            {
                error = $"Operation {operationIndex}: Type '{componentType}' not found";
                return false;
            }

            int index = GetInt(operation, "componentIndex", GetInt(operation, "index", 0));
            var components = go.GetComponents(type);
            if (components == null || index < 0 || index >= components.Length)
            {
                error = $"Operation {operationIndex}: Component '{componentType}' at index {index} not found on '{go.name}'";
                return false;
            }

            component = components[index];
            return true;
        }

        private static bool TryApplySerializedProperties(Component component, Dictionary<string, object> properties,
            List<string> changedProperties, out string error)
        {
            error = "";
            foreach (var pair in properties)
            {
                if (!TryApplySerializedProperty(component, pair.Key, pair.Value, out error))
                    return false;

                changedProperties.Add(pair.Key);
            }

            return true;
        }

        private static bool TryApplySerializedProperty(Component component, string propertyName, object value,
            out string error)
        {
            error = "";
            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propertyName);
            if (prop == null)
            {
                error = $"Property '{propertyName}' not found on '{component.GetType().Name}'";
                return false;
            }

            MCPComponentCommands.SetSerializedValue(prop, value);
            serialized.ApplyModifiedProperties();
            return true;
        }

        private static bool TryResolveBatchReference(GameObject root, SerializedProperty property,
            Dictionary<string, object> operation, out UnityEngine.Object targetRef,
            out string refDescription, out string error)
        {
            targetRef = null;
            refDescription = "null (cleared)";
            error = "";

            bool clearRef = GetBool(operation, "clear", false);
            if (clearRef)
                return true;

            string referenceAssetPath = GetString(operation, "referenceAssetPath");
            if (string.IsNullOrEmpty(referenceAssetPath) == false)
            {
                if (!TryResolveAssetReference(property, referenceAssetPath, out targetRef, out error))
                    return false;

                refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                return true;
            }

            string referencePrefabPath = GetString(operation, "referencePrefabPath");
            if (string.IsNullOrEmpty(referencePrefabPath) == false)
            {
                var refGo = FindInPrefab(root, referencePrefabPath);
                if (refGo == null)
                {
                    error = $"GameObject '{referencePrefabPath}' not found in prefab";
                    return false;
                }

                string referenceComponentType = GetString(operation, "referenceComponentType");
                if (string.IsNullOrEmpty(referenceComponentType) == false)
                {
                    Type refType = MCPComponentCommands.FindType(referenceComponentType);
                    if (refType == null)
                    {
                        error = $"Type '{referenceComponentType}' not found";
                        return false;
                    }

                    targetRef = refGo.GetComponent(refType);
                    if (targetRef == null)
                    {
                        error = $"Component '{referenceComponentType}' not found on '{refGo.name}'";
                        return false;
                    }
                }
                else
                {
                    targetRef = refGo;
                }

                refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                return true;
            }

            error = "Provide referenceAssetPath, referencePrefabPath, or clear=true";
            return false;
        }

        private static bool TryResolveAssetReference(SerializedProperty property, string assetPath,
            out UnityEngine.Object targetRef, out string error)
        {
            targetRef = null;
            error = "";
            var candidates = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (candidates == null || candidates.Length == 0)
            {
                error = $"Asset not found at '{assetPath}'";
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null)
                    continue;

                property.objectReferenceValue = candidate;
                if (property.objectReferenceValue == candidate)
                {
                    targetRef = candidate;
                    return true;
                }
            }

            error = $"No asset at '{assetPath}' is compatible with property " +
                    $"'{property.propertyPath}' ({property.type})";
            return false;
        }

        private static void ApplyTransformArguments(Transform transform, Dictionary<string, object> operation)
        {
            if (operation.ContainsKey("position"))
                transform.localPosition = ParseVector3(operation["position"]);
            if (operation.ContainsKey("rotation"))
                transform.localEulerAngles = ParseVector3(operation["rotation"]);
            if (operation.ContainsKey("scale"))
                transform.localScale = ParseVector3(operation["scale"]);
        }

        private static Dictionary<string, object> BuildBatchSummary(int operationIndex, string operationType,
            GameObject go, Component component)
        {
            var summary = new Dictionary<string, object>
            {
                { "index", operationIndex },
                { "type", operationType },
                { "gameObject", go != null ? go.name : "" },
            };

            if (component != null)
            {
                summary["component"] = component.GetType().Name;
                summary["fullType"] = component.GetType().FullName;
            }

            return summary;
        }

        private static List<string> CollectBatchEditComponentTypes(Dictionary<string, object> args)
        {
            var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in GetDictionaryList(args, "operations"))
            {
                AddComponentType(types, GetString(operation, "componentType"));
                AddComponentType(types, GetString(operation, "referenceComponentType"));
            }

            return types.OrderBy(type => type, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddComponentType(HashSet<string> types, string componentType)
        {
            if (string.IsNullOrEmpty(componentType) == false)
                types.Add(componentType);
        }

        private static string GetOperationType(Dictionary<string, object> operation)
        {
            string operationType = GetString(operation, "type");
            if (string.IsNullOrEmpty(operationType))
                operationType = GetString(operation, "op");
            if (string.IsNullOrEmpty(operationType))
                operationType = GetString(operation, "action");

            return operationType
                .Replace("-", "")
                .Replace("_", "")
                .Replace(" ", "")
                .ToLowerInvariant();
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) ? args[key]?.ToString() : "";
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var results = new List<string>();
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return results;

            if (value is List<object> list)
            {
                foreach (object item in list)
                {
                    if (item != null)
                        results.Add(item.ToString());
                }
            }
            else
            {
                results.Add(value.ToString());
            }

            return results;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return null;

            return value as Dictionary<string, object>;
        }

        private static List<Dictionary<string, object>> GetDictionaryList(Dictionary<string, object> args, string key)
        {
            var results = new List<Dictionary<string, object>>();
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return results;

            if (value is List<object> list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<string, object> dict)
                        results.Add(dict);
                }
            }

            return results;
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
            string diffMode = (GetString(args, "prefabFileDiffMode") ?? "").ToLowerInvariant();
            if (diffMode != "full" && diffMode != "minimal" && diffMode != "summary")
                diffMode = "summary";
            if (diffMode == "minimal")
                contextLines = 0;

            int maxLines = Math.Max(1, Math.Min(GetInt(args, "prefabFileDiffMaxLines", 200), 1000));

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
            var allLines = BuildLineDiff(beforeLines, afterLines);
            var summary = BuildDiffSummary(beforeLines.Length, afterLines.Length, allLines);
            int changedLineCount = Convert.ToInt32(summary["addedLineCount"]) +
                                   Convert.ToInt32(summary["removedLineCount"]);
            result["changed"] = changedLineCount > 0;

            var lines = BuildDisplayDiffLines(allLines, contextLines);
            int ignoredLineCount = FilterDiffLines(lines, args);
            bool truncated = lines.Count > maxLines;
            if (truncated)
                lines.RemoveRange(maxLines, lines.Count - maxLines);
            summary["contextLineCount"] = lines.Count(line =>
                line.TryGetValue("type", out var value) && value?.ToString() == "context");

            result["changedLineCount"] = changedLineCount;
            result["ignoredLineCount"] = ignoredLineCount;
            result["returnedLineCount"] = diffMode == "summary" ? 0 : lines.Count;
            result["truncated"] = truncated;
            result["summary"] = summary;
            result["lines"] = diffMode == "summary" ? new List<Dictionary<string, object>>() : lines;
            return result;
        }

        private static Dictionary<string, object> BuildDiffSummary(int beforeLineCount, int afterLineCount,
            List<Dictionary<string, object>> lines)
        {
            int added = 0;
            int removed = 0;
            int context = 0;

            foreach (var line in lines)
            {
                var type = line.TryGetValue("type", out var value) && value != null ? value.ToString() : "";
                switch (type)
                {
                    case "added":
                        added++;
                        break;
                    case "removed":
                        removed++;
                        break;
                    case "context":
                        context++;
                        break;
                }
            }

            return new Dictionary<string, object>
            {
                { "beforeLineCount", beforeLineCount },
                { "afterLineCount", afterLineCount },
                { "netLineDelta", afterLineCount - beforeLineCount },
                { "addedLineCount", added },
                { "removedLineCount", removed },
                { "contextLineCount", context },
            };
        }

        private static int FilterDiffLines(List<Dictionary<string, object>> lines, Dictionary<string, object> args)
        {
            var ignoreContains = GetStringList(args, "prefabFileDiffIgnoreContains");
            var ignoreProperties = GetStringList(args, "prefabFileDiffIgnoreYamlProperties");
            if (ignoreContains.Count == 0 && ignoreProperties.Count == 0)
                return 0;

            int beforeCount = lines.Count;
            lines.RemoveAll(line =>
            {
                var text = line.TryGetValue("text", out var value) && value != null ? value.ToString() : "";
                return ignoreContains.Any(item => text.Contains(item)) ||
                       ignoreProperties.Any(property => IsYamlPropertyLine(text, property));
            });

            return beforeCount - lines.Count;
        }

        private static bool IsYamlPropertyLine(string text, string propertyName)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(propertyName))
                return false;

            string trimmed = text.TrimStart();
            return trimmed.StartsWith(propertyName + ":", StringComparison.Ordinal) ||
                   trimmed.StartsWith("- " + propertyName + ":", StringComparison.Ordinal);
        }

        private static List<Dictionary<string, object>> BuildLineDiff(string[] beforeLines, string[] afterLines)
        {
            int beforeCount = beforeLines.Length;
            int afterCount = afterLines.Length;
            int maxDepth = beforeCount + afterCount;
            var trace = new List<Dictionary<int, int>>();
            var vector = new Dictionary<int, int> { { 1, 0 } };

            for (int depth = 0; depth <= maxDepth; depth++)
            {
                trace.Add(new Dictionary<int, int>(vector));
                for (int diagonal = -depth; diagonal <= depth; diagonal += 2)
                {
                    int beforeIndex;
                    if (diagonal == -depth ||
                        diagonal != depth && GetDiffVectorValue(vector, diagonal - 1) <
                        GetDiffVectorValue(vector, diagonal + 1))
                    {
                        beforeIndex = GetDiffVectorValue(vector, diagonal + 1);
                    }
                    else
                    {
                        beforeIndex = GetDiffVectorValue(vector, diagonal - 1) + 1;
                    }

                    int afterIndex = beforeIndex - diagonal;
                    while (beforeIndex < beforeCount && afterIndex < afterCount &&
                           beforeLines[beforeIndex] == afterLines[afterIndex])
                    {
                        beforeIndex++;
                        afterIndex++;
                    }

                    vector[diagonal] = beforeIndex;
                    if (beforeIndex >= beforeCount && afterIndex >= afterCount)
                        return BacktrackLineDiff(trace, beforeLines, afterLines);
                }
            }

            return new List<Dictionary<string, object>>();
        }

        private static List<Dictionary<string, object>> BacktrackLineDiff(
            List<Dictionary<int, int>> trace, string[] beforeLines, string[] afterLines)
        {
            int beforeIndex = beforeLines.Length;
            int afterIndex = afterLines.Length;
            var reversed = new List<Dictionary<string, object>>();

            for (int depth = trace.Count - 1; depth >= 0; depth--)
            {
                var vector = trace[depth];
                int diagonal = beforeIndex - afterIndex;
                int previousDiagonal;
                if (diagonal == -depth ||
                    diagonal != depth && GetDiffVectorValue(vector, diagonal - 1) <
                    GetDiffVectorValue(vector, diagonal + 1))
                {
                    previousDiagonal = diagonal + 1;
                }
                else
                {
                    previousDiagonal = diagonal - 1;
                }

                int previousBeforeIndex = GetDiffVectorValue(vector, previousDiagonal);
                int previousAfterIndex = previousBeforeIndex - previousDiagonal;

                while (beforeIndex > previousBeforeIndex && afterIndex > previousAfterIndex)
                {
                    reversed.Add(CreateDiffLine("context", beforeIndex, afterIndex,
                        beforeLines[beforeIndex - 1]));
                    beforeIndex--;
                    afterIndex--;
                }

                if (depth == 0)
                    break;

                if (beforeIndex == previousBeforeIndex)
                {
                    reversed.Add(CreateDiffLine("added", null, afterIndex, afterLines[afterIndex - 1]));
                    afterIndex--;
                }
                else
                {
                    reversed.Add(CreateDiffLine("removed", beforeIndex, null, beforeLines[beforeIndex - 1]));
                    beforeIndex--;
                }
            }

            reversed.Reverse();
            return reversed;
        }

        private static int GetDiffVectorValue(Dictionary<int, int> vector, int diagonal)
        {
            return vector.TryGetValue(diagonal, out int value) ? value : 0;
        }

        private static Dictionary<string, object> CreateDiffLine(string type, int? beforeLine,
            int? afterLine, string text)
        {
            return new Dictionary<string, object>
            {
                { "type", type },
                { "beforeLine", beforeLine.HasValue ? beforeLine.Value : (object)null },
                { "afterLine", afterLine.HasValue ? afterLine.Value : (object)null },
                { "text", text },
            };
        }

        private static List<Dictionary<string, object>> BuildDisplayDiffLines(
            List<Dictionary<string, object>> allLines, int contextLines)
        {
            if (allLines.Count == 0)
                return new List<Dictionary<string, object>>();

            var included = new bool[allLines.Count];
            for (int i = 0; i < allLines.Count; i++)
            {
                string type = allLines[i].TryGetValue("type", out var value) ? value?.ToString() : "";
                if (type == "context")
                    continue;

                int start = Math.Max(0, i - contextLines);
                int end = Math.Min(allLines.Count - 1, i + contextLines);
                for (int lineIndex = start; lineIndex <= end; lineIndex++)
                    included[lineIndex] = true;
            }

            var lines = new List<Dictionary<string, object>>();
            for (int i = 0; i < allLines.Count; i++)
            {
                if (included[i])
                    lines.Add(allLines[i]);
            }
            return lines;
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
                snapshot.Bytes = snapshot.Exists ? File.ReadAllBytes(snapshot.AbsolutePath) : Array.Empty<byte>();
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

            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(line => line.TrimEnd(' ', '\t')).ToArray();
        }

        private sealed class AssetTextSnapshot
        {
            public string AssetPath;
            public string AbsolutePath;
            public bool Exists;
            public byte[] Bytes;
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
            };
            MCPTransformSerialization.AddLocal(result, go.transform);

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
