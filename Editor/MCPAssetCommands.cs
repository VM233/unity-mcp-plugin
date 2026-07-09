using System;
using System.Collections;
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

        public static object Refresh(Dictionary<string, object> args)
        {
            bool forceUpdate = GetBool(args, "forceUpdate", true);
            bool saveAssets = GetBool(args, "saveAssets", false);
            var assetPaths = GetStringList(args, "assetPaths");

            string singlePath = GetFirstString(args, "assetPath", "path");
            if (!string.IsNullOrEmpty(singlePath))
                assetPaths.Add(singlePath);

            var options = forceUpdate ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default;
            var importedPaths = new List<string>();

            if (assetPaths.Count > 0)
            {
                foreach (string rawPath in assetPaths)
                {
                    string path = NormalizeAssetPath(rawPath);
                    if (string.IsNullOrEmpty(path))
                        continue;

                    AssetDatabase.ImportAsset(path, options);
                    importedPaths.Add(path);
                }
            }
            else
            {
                AssetDatabase.Refresh(options);
            }

            if (saveAssets)
                AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "forceUpdate", forceUpdate },
                { "saveAssets", saveAssets },
                { "importedPaths", importedPaths },
                { "refreshedAllAssets", importedPaths.Count == 0 },
                { "isUpdating", EditorApplication.isUpdating },
                { "isCompiling", EditorApplication.isCompiling },
            };
        }

        public static object ExportUnityPackage(Dictionary<string, object> args)
        {
            var assetPaths = GetStringList(args, "assetPaths");
            if (assetPaths.Count == 0)
            {
                string singlePath = GetString(args, "assetPath");
                if (string.IsNullOrEmpty(singlePath))
                    singlePath = GetString(args, "path");
                if (!string.IsNullOrEmpty(singlePath))
                    assetPaths.Add(singlePath);
            }

            string outputPath = GetString(args, "outputPath");
            if (string.IsNullOrEmpty(outputPath))
                outputPath = GetString(args, "filePath");

            if (assetPaths.Count == 0)
                return new { error = "assetPaths, assetPath, or path is required" };
            if (string.IsNullOrEmpty(outputPath))
                return new { error = "outputPath is required" };

            var normalizedPaths = new List<string>();
            var missingPaths = new List<string>();
            foreach (string assetPath in assetPaths)
            {
                string normalizedPath = NormalizeAssetPath(assetPath);
                if (string.IsNullOrEmpty(normalizedPath))
                    continue;

                if (!AssetExists(normalizedPath))
                    missingPaths.Add(normalizedPath);
                else if (!normalizedPaths.Contains(normalizedPath))
                    normalizedPaths.Add(normalizedPath);
            }

            if (normalizedPaths.Count == 0)
                return new { error = "No valid asset paths were provided" };
            if (missingPaths.Count > 0)
            {
                return new Dictionary<string, object>
                {
                    { "error", "One or more asset paths were not found" },
                    { "missingPaths", missingPaths },
                };
            }

            string fullOutputPath = NormalizeUnityPackageOutputPath(outputPath);
            bool overwrite = GetBool(args, "overwrite", false);
            if (File.Exists(fullOutputPath))
            {
                if (!overwrite)
                    return new { error = $"Output file already exists: '{fullOutputPath}'. Pass overwrite=true to replace it." };

                File.Delete(fullOutputPath);
            }

            string outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory) && !Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            bool includeDependencies = GetBool(args, "includeDependencies", true);
            bool recurse = GetBool(args, "recurse", true);
            bool interactive = GetBool(args, "interactive", false);

            var options = ExportPackageOptions.Default;
            if (includeDependencies)
                options |= ExportPackageOptions.IncludeDependencies;
            if (recurse)
                options |= ExportPackageOptions.Recurse;
            if (interactive)
                options |= ExportPackageOptions.Interactive;

            AssetDatabase.ExportPackage(normalizedPaths.ToArray(), fullOutputPath, options);

            bool exported = File.Exists(fullOutputPath);
            long size = exported ? new FileInfo(fullOutputPath).Length : 0;
            return new Dictionary<string, object>
            {
                { "success", exported },
                { "assetPaths", normalizedPaths },
                { "outputPath", fullOutputPath },
                { "size", size },
                { "includeDependencies", includeDependencies },
                { "recurse", recurse },
                { "interactive", interactive },
            };
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
            string path = NormalizeAssetPath(GetFirstString(args, "path", "assetPath"));
            string newName = GetFirstString(args, "newName", "name", "newAssetName");
            bool dryRun = GetBool(args, "dryRun", false);

            if (string.IsNullOrEmpty(path))
                return new { error = "path or assetPath is required" };
            if (string.IsNullOrEmpty(newName))
                return new { error = "newName or name is required" };
            if (newName.Contains("/") || newName.Contains("\\"))
                return new { error = "newName must be a file or folder name, not a path" };
            if (!AssetExists(path))
                return new { error = $"Asset not found at '{path}'" };

            string oldGuid = AssetDatabase.AssetPathToGUID(path);
            string oldMetaPath = GetMetaPath(path);
            bool oldMetaExists = File.Exists(GetAbsolutePath(oldMetaPath));
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

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "dryRun", true },
                    { "oldPath", path },
                    { "expectedPath", expectedPath },
                    { "oldGuid", oldGuid },
                    { "oldMetaPath", oldMetaPath },
                    { "expectedMetaPath", GetMetaPath(expectedPath) },
                    { "oldMetaExists", oldMetaExists },
                };
            }

            string error = AssetDatabase.RenameAsset(path, renameName);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newPath = AssetDatabase.GUIDToAssetPath(oldGuid);
            string newGuid = string.IsNullOrEmpty(newPath) ? "" : AssetDatabase.AssetPathToGUID(newPath);
            bool guidChanged = !string.Equals(oldGuid, newGuid, StringComparison.Ordinal);
            string newMetaPath = string.IsNullOrEmpty(newPath) ? "" : GetMetaPath(newPath);
            bool newMetaExists = !string.IsNullOrEmpty(newMetaPath) && File.Exists(GetAbsolutePath(newMetaPath));

            return new Dictionary<string, object>
            {
                { "success", true },
                { "dryRun", false },
                { "oldPath", path },
                { "newPath", newPath },
                { "expectedPath", expectedPath },
                { "actualPathMatchesExpected", string.Equals(newPath, expectedPath, StringComparison.OrdinalIgnoreCase) },
                { "oldGuid", oldGuid },
                { "newGuid", newGuid },
                { "guidChanged", guidChanged },
                { "metaPreserved", !guidChanged },
                { "oldMetaPath", oldMetaPath },
                { "newMetaPath", newMetaPath },
                { "oldMetaExists", oldMetaExists },
                { "newMetaExists", newMetaExists },
            };
        }

        public static object Move(Dictionary<string, object> args)
        {
            string path = NormalizeAssetPath(GetFirstString(args, "path", "assetPath"));
            string destinationPath = NormalizeAssetPath(GetFirstString(args, "destinationPath", "targetPath",
                "destinationFolder", "targetFolder", "folder"));
            bool dryRun = GetBool(args, "dryRun", false);

            if (string.IsNullOrEmpty(path))
                return new { error = "path or assetPath is required" };
            if (string.IsNullOrEmpty(destinationPath))
                return new { error = "destinationPath, targetPath, or destinationFolder is required" };
            if (!AssetExists(path))
                return new { error = $"Asset not found at '{path}'" };

            string oldGuid = AssetDatabase.AssetPathToGUID(path);
            string oldMetaPath = GetMetaPath(path);
            bool oldMetaExists = File.Exists(GetAbsolutePath(oldMetaPath));
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

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "dryRun", true },
                    { "oldPath", path },
                    { "requestedDestinationPath", destinationPath },
                    { "targetPath", targetPath },
                    { "oldGuid", oldGuid },
                    { "oldMetaPath", oldMetaPath },
                    { "targetMetaPath", GetMetaPath(targetPath) },
                    { "oldMetaExists", oldMetaExists },
                };
            }

            string error = AssetDatabase.MoveAsset(path, targetPath);
            if (!string.IsNullOrEmpty(error))
                return new { error };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string newPath = AssetDatabase.GUIDToAssetPath(oldGuid);
            string newGuid = string.IsNullOrEmpty(newPath) ? "" : AssetDatabase.AssetPathToGUID(newPath);
            bool guidChanged = !string.Equals(oldGuid, newGuid, StringComparison.Ordinal);
            string newMetaPath = string.IsNullOrEmpty(newPath) ? "" : GetMetaPath(newPath);
            bool newMetaExists = !string.IsNullOrEmpty(newMetaPath) && File.Exists(GetAbsolutePath(newMetaPath));

            return new Dictionary<string, object>
            {
                { "success", true },
                { "dryRun", false },
                { "oldPath", path },
                { "requestedDestinationPath", destinationPath },
                { "newPath", newPath },
                { "targetPath", targetPath },
                { "actualPathMatchesTarget", string.Equals(newPath, targetPath, StringComparison.OrdinalIgnoreCase) },
                { "oldGuid", oldGuid },
                { "newGuid", newGuid },
                { "guidChanged", guidChanged },
                { "metaPreserved", !guidChanged },
                { "oldMetaPath", oldMetaPath },
                { "newMetaPath", newMetaPath },
                { "oldMetaExists", oldMetaExists },
                { "newMetaExists", newMetaExists },
            };
        }

        public static object MoveBatch(Dictionary<string, object> args)
        {
            bool dryRun = GetBool(args, "dryRun", false);
            List<Dictionary<string, object>> requestedMoves = GetDictionaryList(args, "moves");
            if (requestedMoves.Count == 0)
                return new { error = "moves must contain at least one move request" };

            var entries = new List<BatchMoveEntry>();
            for (int index = 0; index < requestedMoves.Count; index++)
            {
                var request = requestedMoves[index];
                string path = NormalizeAssetPath(GetFirstString(request, "path", "assetPath"));
                string destinationPath = NormalizeAssetPath(GetFirstString(request, "destinationPath", "targetPath",
                    "destinationFolder", "targetFolder", "folder"));

                if (string.IsNullOrEmpty(path))
                    return BatchMoveValidationError(index, "path or assetPath is required");
                if (string.IsNullOrEmpty(destinationPath))
                    return BatchMoveValidationError(index, "destinationPath, targetPath, or destinationFolder is required");
                if (!AssetExists(path))
                    return BatchMoveValidationError(index, $"Asset not found at '{path}'");

                string targetPath = NormalizeMoveTargetPath(path, destinationPath);
                string targetDirectory = Path.GetDirectoryName(targetPath)?.Replace('\\', '/') ?? "";
                bool sourceIsFolder = AssetDatabase.IsValidFolder(path);
                if (!sourceIsFolder && !AssetDatabase.IsValidFolder(destinationPath))
                {
                    string sourceExtension = Path.GetExtension(path);
                    string targetExtension = Path.GetExtension(targetPath);
                    if (string.IsNullOrEmpty(targetExtension))
                        return BatchMoveValidationError(index, "destinationPath must be an existing folder or include the asset file extension");
                    if (!string.Equals(sourceExtension, targetExtension, StringComparison.OrdinalIgnoreCase))
                        return BatchMoveValidationError(index,
                            $"Changing file extension is not supported: '{sourceExtension}' to '{targetExtension}'");
                }

                if (!string.IsNullOrEmpty(targetDirectory) && !AssetDatabase.IsValidFolder(targetDirectory))
                    return BatchMoveValidationError(index, $"Target directory does not exist: '{targetDirectory}'");
                if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                    return BatchMoveValidationError(index, "Source and target paths are the same");
                if (AssetExists(targetPath))
                    return BatchMoveValidationError(index, $"Target asset already exists at '{targetPath}'");

                entries.Add(new BatchMoveEntry
                {
                    Index = index,
                    OldPath = path,
                    RequestedDestinationPath = destinationPath,
                    TargetPath = targetPath,
                    OldGuid = AssetDatabase.AssetPathToGUID(path),
                    OldMetaPath = GetMetaPath(path),
                    OldMetaExists = File.Exists(GetAbsolutePath(GetMetaPath(path)));
                });
            }

            for (int index = 0; index < entries.Count; index++)
            {
                for (int otherIndex = index + 1; otherIndex < entries.Count; otherIndex++)
                {
                    if (string.Equals(entries[index].OldPath, entries[otherIndex].OldPath,
                            StringComparison.OrdinalIgnoreCase))
                        return BatchMoveValidationError(otherIndex, $"Duplicate source path '{entries[otherIndex].OldPath}'");
                    if (string.Equals(entries[index].TargetPath, entries[otherIndex].TargetPath,
                            StringComparison.OrdinalIgnoreCase))
                        return BatchMoveValidationError(otherIndex, $"Duplicate target path '{entries[otherIndex].TargetPath}'");
                }
            }

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "dryRun", true },
                    { "moves", entries.ConvertAll(CreateBatchMoveResult) },
                };
            }

            string moveError = "";
            var rollbackErrors = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (BatchMoveEntry entry in entries)
                {
                    string error = AssetDatabase.MoveAsset(entry.OldPath, entry.TargetPath);
                    if (!string.IsNullOrEmpty(error))
                    {
                        moveError = $"Move {entry.Index} failed: {error}";
                        break;
                    }

                    entry.Moved = true;
                }

                if (!string.IsNullOrEmpty(moveError))
                {
                    for (int index = entries.Count - 1; index >= 0; index--)
                    {
                        BatchMoveEntry entry = entries[index];
                        if (!entry.Moved)
                            continue;

                        string rollbackError = AssetDatabase.MoveAsset(entry.TargetPath, entry.OldPath);
                        if (string.IsNullOrEmpty(rollbackError))
                            entry.RolledBack = true;
                        else
                            rollbackErrors.Add($"Rollback {entry.Index} failed: {rollbackError}");
                    }
                }
            }
            catch (Exception exception)
            {
                moveError = $"Batch move threw: {exception.Message}";
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    BatchMoveEntry entry = entries[index];
                    if (!entry.Moved || entry.RolledBack)
                        continue;

                    string rollbackError = AssetDatabase.MoveAsset(entry.TargetPath, entry.OldPath);
                    if (string.IsNullOrEmpty(rollbackError))
                        entry.RolledBack = true;
                    else
                        rollbackErrors.Add($"Rollback {entry.Index} failed: {rollbackError}");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (!string.IsNullOrEmpty(moveError))
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", moveError },
                    { "rolledBack", rollbackErrors.Count == 0 },
                    { "rollbackErrors", rollbackErrors },
                    { "moves", entries.ConvertAll(CreateBatchMoveResult) },
                };
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "dryRun", false },
                { "moves", entries.ConvertAll(CreateBatchMoveResult) },
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

        private static string GetFirstString(Dictionary<string, object> args, params string[] keys)
        {
            if (args == null || keys == null)
                return "";

            foreach (string key in keys)
            {
                string value = GetString(args, key);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }

            return "";
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            try
            {
                return Convert.ToBoolean(args[key]);
            }
            catch
            {
                return defaultValue;
            }
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            var result = new List<string>();
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return result;

            object value = args[key];
            if (value is string stringValue)
            {
                if (!string.IsNullOrWhiteSpace(stringValue))
                    result.Add(stringValue);
                return result;
            }

            var enumerable = value as IEnumerable;
            if (enumerable == null)
                return result;

            foreach (object item in enumerable)
            {
                if (item == null)
                    continue;

                string text = item.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }

            return result;
        }

        private static List<Dictionary<string, object>> GetDictionaryList(Dictionary<string, object> args, string key)
        {
            var result = new List<Dictionary<string, object>>();
            if (args == null || !args.TryGetValue(key, out object value) || !(value is IEnumerable enumerable))
                return result;

            foreach (object item in enumerable)
            {
                if (item is Dictionary<string, object> dictionary)
                {
                    result.Add(dictionary);
                    continue;
                }

                if (!(item is IDictionary dictionaryValue))
                    continue;

                var converted = new Dictionary<string, object>();
                foreach (DictionaryEntry pair in dictionaryValue)
                {
                    if (pair.Key != null)
                        converted[pair.Key.ToString()] = pair.Value;
                }

                result.Add(converted);
            }

            return result;
        }

        private static bool AssetExists(string path)
        {
            return AssetDatabase.IsValidFolder(path) || AssetDatabase.LoadMainAssetAtPath(path) != null;
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            return path.Replace('\\', '/').Trim().Trim('/');
        }

        private static string GetMetaPath(string assetPath)
        {
            return string.IsNullOrEmpty(assetPath) ? "" : NormalizeAssetPath(assetPath) + ".meta";
        }

        private static string GetAbsolutePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return "";

            if (Path.IsPathRooted(assetPath))
                return Path.GetFullPath(assetPath);

            return Path.GetFullPath(Path.Combine(GetProjectRoot(), NormalizeAssetPath(assetPath)));
        }

        private static string NormalizeUnityPackageOutputPath(string outputPath)
        {
            string normalized = outputPath.Replace('\\', '/').Trim();
            if (!string.Equals(Path.GetExtension(normalized), ".unitypackage", StringComparison.OrdinalIgnoreCase))
                normalized += ".unitypackage";

            if (!Path.IsPathRooted(normalized))
                normalized = Path.Combine(GetProjectRoot(), normalized);

            return Path.GetFullPath(normalized);
        }

        private static string GetProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string NormalizeMoveTargetPath(string sourcePath, string destinationPath)
        {
            destinationPath = destinationPath.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(destinationPath))
                return destinationPath.TrimEnd('/') + "/" + Path.GetFileName(sourcePath);

            return destinationPath;
        }

        private static object BatchMoveValidationError(int index, string error)
        {
            return new { error = $"Move {index} is invalid: {error}" };
        }

        private static Dictionary<string, object> CreateBatchMoveResult(BatchMoveEntry entry)
        {
            string currentPath = AssetDatabase.GUIDToAssetPath(entry.OldGuid);
            string currentGuid = string.IsNullOrEmpty(currentPath) ? "" : AssetDatabase.AssetPathToGUID(currentPath);
            string currentMetaPath = string.IsNullOrEmpty(currentPath) ? "" : GetMetaPath(currentPath);
            return new Dictionary<string, object>
            {
                { "index", entry.Index },
                { "oldPath", entry.OldPath },
                { "requestedDestinationPath", entry.RequestedDestinationPath },
                { "targetPath", entry.TargetPath },
                { "currentPath", currentPath },
                { "oldGuid", entry.OldGuid },
                { "currentGuid", currentGuid },
                { "guidChanged", !string.Equals(entry.OldGuid, currentGuid, StringComparison.Ordinal) },
                { "metaPreserved", string.Equals(entry.OldGuid, currentGuid, StringComparison.Ordinal) },
                { "oldMetaPath", entry.OldMetaPath },
                { "currentMetaPath", currentMetaPath },
                { "oldMetaExists", entry.OldMetaExists },
                { "currentMetaExists", !string.IsNullOrEmpty(currentMetaPath) && File.Exists(GetAbsolutePath(currentMetaPath)) },
                { "moved", entry.Moved },
                { "rolledBack", entry.RolledBack },
            };
        }

        private sealed class BatchMoveEntry
        {
            public int Index;
            public string OldPath;
            public string RequestedDestinationPath;
            public string TargetPath;
            public string OldGuid;
            public string OldMetaPath;
            public bool OldMetaExists;
            public bool Moved;
            public bool RolledBack;
        }
    }
}
