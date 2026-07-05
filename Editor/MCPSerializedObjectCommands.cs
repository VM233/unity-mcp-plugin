using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPSerializedObjectCommands
    {
        public static object Get(Dictionary<string, object> args)
        {
            if (!TryResolveTarget(args, out var target, out string error))
                return new { error };

            string propertyPath = GetString(args, "propertyPath");
            int maxProperties = Math.Max(1, GetInt(args, "maxProperties", 200));
            bool includeChildren = GetBool(args, "includeChildren", true);

            var serialized = new SerializedObject(target);
            var result = BuildTargetResult(target);

            if (!string.IsNullOrEmpty(propertyPath))
            {
                var property = serialized.FindProperty(propertyPath);
                if (property == null)
                    return new { error = $"Property '{propertyPath}' was not found on '{target.GetType().Name}'" };

                result["property"] = BuildPropertyInfo(property);
                return result;
            }

            var properties = new List<Dictionary<string, object>>();
            var iterator = serialized.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    properties.Add(BuildPropertyInfo(iterator));
                    if (properties.Count >= maxProperties)
                        break;
                } while (iterator.NextVisible(includeChildren));
            }

            result["propertyCount"] = properties.Count;
            result["truncated"] = properties.Count >= maxProperties;
            result["properties"] = properties;
            return result;
        }

        public static object Set(Dictionary<string, object> args)
        {
            if (!TryResolveTarget(args, out var target, out string error))
                return new { error };

            string propertyPath = GetString(args, "propertyPath");
            if (string.IsNullOrEmpty(propertyPath))
                return new { error = "propertyPath is required" };
            if (args == null || !args.ContainsKey("value"))
                return new { error = "value is required" };

            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(propertyPath);
            if (property == null)
                return new { error = $"Property '{propertyPath}' was not found on '{target.GetType().Name}'" };

            var beforeValue = MCPComponentCommands.GetSerializedValue(property);
            MCPComponentCommands.SetSerializedValue(property, args["value"]);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            AssetDatabase.SaveAssets();

            serialized.Update();
            property = serialized.FindProperty(propertyPath);

            var result = BuildTargetResult(target);
            result["success"] = true;
            result["propertyPath"] = propertyPath;
            result["beforeValue"] = beforeValue;
            result["afterValue"] = property == null ? null : MCPComponentCommands.GetSerializedValue(property);
            result["property"] = property == null ? null : BuildPropertyInfo(property);
            return result;
        }

        private static bool TryResolveTarget(Dictionary<string, object> args, out UnityEngine.Object target,
            out string error)
        {
            target = null;
            error = "";

            int instanceId = GetInt(args, "instanceId", 0);
            if (instanceId != 0)
            {
                target = EditorUtility.InstanceIDToObject(instanceId);
                if (target != null)
                    return true;

                error = $"Object instanceId '{instanceId}' was not found";
                return false;
            }

            string gameObjectPath = GetString(args, "gameObjectPath");
            if (string.IsNullOrEmpty(gameObjectPath) == false)
            {
                var gameObject = GameObject.Find(gameObjectPath);
                if (gameObject == null)
                {
                    error = $"GameObject '{gameObjectPath}' was not found";
                    return false;
                }

                target = ResolveComponentOrGameObject(gameObject, args, out error);
                return target != null;
            }

            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath) == false)
            {
                target = LoadAssetTarget(assetPath, args, out error);
                return target != null;
            }

            error = "Provide instanceId, gameObjectPath, or assetPath";
            return false;
        }

        private static UnityEngine.Object LoadAssetTarget(string assetPath, Dictionary<string, object> args,
            out string error)
        {
            error = "";
            Type assetType = ResolveType(GetString(args, "assetType"));
            var asset = assetType == null
                ? AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath)
                : AssetDatabase.LoadAssetAtPath(assetPath, assetType);

            if (asset == null)
            {
                error = $"Asset '{assetPath}' was not found";
                return null;
            }

            if (asset is GameObject gameObject)
                return ResolveComponentOrGameObject(gameObject, args, out error);

            return asset;
        }

        private static UnityEngine.Object ResolveComponentOrGameObject(GameObject gameObject,
            Dictionary<string, object> args, out string error)
        {
            error = "";
            string componentTypeName = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentTypeName))
                return gameObject;

            Type componentType = MCPComponentCommands.FindType(componentTypeName);
            if (componentType == null)
            {
                error = $"Component type '{componentTypeName}' was not found";
                return null;
            }

            int componentIndex = GetInt(args, "componentIndex", 0);
            var components = gameObject.GetComponents(componentType);
            if (componentIndex < 0 || componentIndex >= components.Length)
            {
                error = $"Component '{componentTypeName}' at index {componentIndex} was not found on '{gameObject.name}'";
                return null;
            }

            return components[componentIndex];
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            return MCPComponentCommands.FindType(typeName) ??
                   AppDomain.CurrentDomain.GetAssemblies()
                       .SelectMany(GetLoadableTypes)
                       .FirstOrDefault(type => string.Equals(type.FullName, typeName, StringComparison.Ordinal) ||
                                               string.Equals(type.Name, typeName, StringComparison.Ordinal));
        }

        private static IEnumerable<Type> GetLoadableTypes(System.Reflection.Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (System.Reflection.ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static Dictionary<string, object> BuildTargetResult(UnityEngine.Object target)
        {
            return new Dictionary<string, object>
            {
                { "success", true },
                { "targetName", target.name },
                { "targetType", target.GetType().Name },
                { "targetFullType", target.GetType().FullName },
                { "instanceId", MCPObjectId.Get(target) },
                { "assetPath", AssetDatabase.GetAssetPath(target) },
            };
        }

        private static Dictionary<string, object> BuildPropertyInfo(SerializedProperty property)
        {
            return new Dictionary<string, object>
            {
                { "name", property.name },
                { "displayName", property.displayName },
                { "propertyPath", property.propertyPath },
                { "type", property.propertyType.ToString() },
                { "editable", property.editable },
                { "isArray", property.isArray },
                { "arraySize", property.isArray ? property.arraySize : -1 },
                { "value", MCPComponentCommands.GetSerializedValue(property) },
            };
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out var value) && value != null ? value.ToString() : "";
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            if (value is bool boolValue)
                return boolValue;

            return bool.TryParse(value.ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out var value) || value == null)
                return defaultValue;

            return int.TryParse(value.ToString(), out int parsed) ? parsed : defaultValue;
        }
    }
}
