using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPAssetCommands
    {
        public static object List(Dictionary<string, object> args)
        {
            string folder = args.ContainsKey("folder") ? args["folder"].ToString() : "Assets";
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString() : null;
            string search = args.ContainsKey("search") ? args["search"].ToString() : null;
            bool recursive = !args.ContainsKey("recursive") || Convert.ToBoolean(args["recursive"]);

            string searchQuery = "";
            if (!string.IsNullOrEmpty(search))
                searchQuery = search;
            if (!string.IsNullOrEmpty(typeFilter))
                searchQuery += $" t:{typeFilter}";

            string[] guids;
            if (!string.IsNullOrEmpty(searchQuery))
            {
                string[] searchFolders = recursive ? new[] { folder } : new[] { folder };
                guids = AssetDatabase.FindAssets(searchQuery.Trim(), searchFolders);
            }
            else
            {
                guids = AssetDatabase.FindAssets("", new[] { folder });
            }

            var assets = new List<Dictionary<string, object>>();
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // If not recursive, only include direct children
                if (!recursive)
                {
                    string parentDir = Path.GetDirectoryName(path).Replace("\\", "/");
                    if (parentDir != folder) continue;
                }

                var assetType = AssetDatabase.GetMainAssetTypeAtPath(path);
                assets.Add(new Dictionary<string, object>
                {
                    { "path", path },
                    { "name", Path.GetFileName(path) },
                    { "type", assetType?.Name ?? "Unknown" },
                    { "guid", guid },
                    { "isFolder", AssetDatabase.IsValidFolder(path) },
                });
            }

            return new Dictionary<string, object>
            {
                { "folder", folder },
                { "count", assets.Count },
                { "assets", assets },
            };
        }

        public static object Import(Dictionary<string, object> args)
        {
            string source = args.ContainsKey("sourcePath") ? args["sourcePath"].ToString() : "";
            string dest = args.ContainsKey("destinationPath") ? args["destinationPath"].ToString() : "";

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest))
                return new { error = "sourcePath and destinationPath are required" };

            string fullDest = Path.Combine(Application.dataPath.Replace("/Assets", ""), dest);
            string destDir = Path.GetDirectoryName(fullDest);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            File.Copy(source, fullDest, true);
            AssetDatabase.ImportAsset(dest);

            return new { success = true, importedPath = dest };
        }

        public static object Delete(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            bool deleted = AssetDatabase.DeleteAsset(path);
            return new { success = deleted, path };
        }

        public static object Rename(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            string newName = GetString(args, "newName");

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };
            if (string.IsNullOrEmpty(newName))
                return new { error = "newName is required" };
            if (newName.Contains("/") || newName.Contains("\\"))
                return new { error = "newName must be a file or folder name, not a path" };
            if (!AssetExists(path))
                return new { error = $"Asset not found at '{path}'" };

            string oldGuid = AssetDatabase.AssetPathToGUID(path);
            bool isFolder = AssetDatabase.IsValidFolder(path);
            string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            string extension = isFolder ? "" : Path.GetExtension(path);
            string newExtension = isFolder ? "" : Path.GetExtension(newName);

            if (!isFolder && !string.IsNullOrEmpty(newExtension) &&
                !string.Equals(newExtension, extension, StringComparison.OrdinalIgnoreCase))
            {
                return new { error = $"Changing file extension is not supported: '{extension}' to '{newExtension}'" };
            }

            string renameName = isFolder ? newName : Path.GetFileNameWithoutExtension(newName);
            string expectedPath = string.IsNullOrEmpty(directory)
                ? renameName + extension
                : directory + "/" + renameName + extension;

            if (!string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase) &&
                AssetExists(expectedPath))
            {
                return new { error = $"Target asset already exists at '{expectedPath}'" };
            }

            string error = AssetDatabase.RenameAsset(path, renameName);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newPath = AssetDatabase.GUIDToAssetPath(oldGuid);
            string newGuid = string.IsNullOrEmpty(newPath) ? "" : AssetDatabase.AssetPathToGUID(newPath);
            bool guidChanged = !string.Equals(oldGuid, newGuid, StringComparison.Ordinal);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "oldPath", path },
                { "newPath", newPath },
                { "expectedPath", expectedPath },
                { "oldGuid", oldGuid },
                { "newGuid", newGuid },
                { "guidChanged", guidChanged },
                { "metaPreserved", !guidChanged },
            };
        }

        public static object Move(Dictionary<string, object> args)
        {
            string path = GetString(args, "path");
            string destinationPath = GetString(args, "destinationPath");

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };
            if (string.IsNullOrEmpty(destinationPath))
                return new { error = "destinationPath is required" };
            if (!AssetExists(path))
                return new { error = $"Asset not found at '{path}'" };

            string oldGuid = AssetDatabase.AssetPathToGUID(path);
            string targetPath = NormalizeMoveTargetPath(path, destinationPath);
            string targetDirectory = Path.GetDirectoryName(targetPath)?.Replace('\\', '/') ?? "";
            bool sourceIsFolder = AssetDatabase.IsValidFolder(path);

            if (!sourceIsFolder && !AssetDatabase.IsValidFolder(destinationPath))
            {
                string sourceExtension = Path.GetExtension(path);
                string targetExtension = Path.GetExtension(targetPath);
                if (string.IsNullOrEmpty(targetExtension))
                    return new { error = "destinationPath must be an existing folder or include the asset file extension" };
                if (!string.Equals(sourceExtension, targetExtension, StringComparison.OrdinalIgnoreCase))
                    return new { error = $"Changing file extension is not supported: '{sourceExtension}' to '{targetExtension}'" };
            }

            if (!string.IsNullOrEmpty(targetDirectory) && !AssetDatabase.IsValidFolder(targetDirectory))
                return new { error = $"Target directory does not exist: '{targetDirectory}'" };
            if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase) && AssetExists(targetPath))
                return new { error = $"Target asset already exists at '{targetPath}'" };

            string error = AssetDatabase.MoveAsset(path, targetPath);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newPath = AssetDatabase.GUIDToAssetPath(oldGuid);
            string newGuid = string.IsNullOrEmpty(newPath) ? "" : AssetDatabase.AssetPathToGUID(newPath);
            bool guidChanged = !string.Equals(oldGuid, newGuid, StringComparison.Ordinal);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "oldPath", path },
                { "requestedDestinationPath", destinationPath },
                { "newPath", newPath },
                { "oldGuid", oldGuid },
                { "newGuid", newGuid },
                { "guidChanged", guidChanged },
                { "metaPreserved", !guidChanged },
            };
        }

        public static object CreatePrefab(Dictionary<string, object> args)
        {
            string goPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";
            string savePath = args.ContainsKey("savePath") ? args["savePath"].ToString() : "";

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            if (string.IsNullOrEmpty(savePath))
                return new { error = "savePath is required" };

            // Ensure directory exists
            string dir = Path.GetDirectoryName(savePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(go, savePath);
            return new Dictionary<string, object>
            {
                { "success", prefab != null },
                { "path", savePath },
                { "name", prefab?.name },
            };
        }

        public static object InstantiatePrefab(Dictionary<string, object> args)
        {
            string prefabPath = args.ContainsKey("prefabPath") ? args["prefabPath"].ToString() : "";
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required" };

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return new { error = $"Prefab not found at {prefabPath}" };

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null) return new { error = "Failed to instantiate prefab" };

            if (args.ContainsKey("name"))
                instance.name = args["name"].ToString();

            if (args.ContainsKey("position"))
                instance.transform.position = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);

            if (args.ContainsKey("rotation"))
                instance.transform.eulerAngles = MCPGameObjectCommands.DictToVector3(args["rotation"] as Dictionary<string, object>);

            if (args.ContainsKey("parent"))
            {
                var parent = GameObject.Find(args["parent"].ToString());
                if (parent != null) instance.transform.SetParent(parent.transform);
            }

            Undo.RegisterCreatedObjectUndo(instance, $"Instantiate {prefab.name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", instance.name },
                { "instanceId", MCPObjectId.Get(instance) },
                { "position", MCPGameObjectCommands.Vector3ToDict(instance.transform.position) },
            };
        }

        public static object CreateMaterial(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string shaderName = args.ContainsKey("shader") ? args["shader"].ToString() : "Standard";

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var shader = Shader.Find(shaderName);
            if (shader == null) return new { error = $"Shader '{shaderName}' not found" };

            var material = new Material(shader);

            if (args.ContainsKey("color"))
            {
                var cd = args["color"] as Dictionary<string, object>;
                if (cd != null)
                {
                    material.color = new Color(
                        Convert.ToSingle(cd.GetValueOrDefault("r", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("g", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("b", 1f)),
                        Convert.ToSingle(cd.GetValueOrDefault("a", 1f))
                    );
                }
            }

            // Ensure directory exists (normalize backslashes from Path.GetDirectoryName on Windows)
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }

            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();

            return new { success = true, path, shader = shaderName };
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) ? args[key]?.ToString() : "";
        }

        private static bool AssetExists(string path)
        {
            return AssetDatabase.IsValidFolder(path) || AssetDatabase.LoadMainAssetAtPath(path) != null;
        }

        private static string NormalizeMoveTargetPath(string sourcePath, string destinationPath)
        {
            destinationPath = destinationPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(destinationPath))
                return destinationPath.TrimEnd('/') + "/" + Path.GetFileName(sourcePath);

            return destinationPath;
        }
    }
}
