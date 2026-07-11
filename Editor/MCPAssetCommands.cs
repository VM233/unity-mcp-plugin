using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            var importerSettings = ConfigureTextureImporter(dest, args);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "importedPath", dest },
                { "importer", importerSettings },
                { "subAssets", DescribeSubAssets(dest) }
            };
        }

        private static object ConfigureTextureImporter(string assetPath, Dictionary<string, object> args)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer)
                return null;

            bool changed = false;
            string textureType = GetString(args, "textureType");
            if (!string.IsNullOrWhiteSpace(textureType))
            {
                if (!Enum.TryParse(textureType, true, out TextureImporterType parsedTextureType))
                    throw new ArgumentException($"Unknown textureType '{textureType}'.");
                importer.textureType = parsedTextureType;
                changed = true;
            }

            string spriteMode = GetString(args, "spriteMode");
            if (!string.IsNullOrWhiteSpace(spriteMode))
            {
                if (!Enum.TryParse(spriteMode, true, out SpriteImportMode parsedSpriteMode))
                    throw new ArgumentException($"Unknown spriteMode '{spriteMode}'.");
                importer.spriteImportMode = parsedSpriteMode;
                changed = true;
            }

            if (args.ContainsKey("pixelsPerUnit"))
            {
                importer.spritePixelsPerUnit = Mathf.Max(0.0001f,
                    Convert.ToSingle(args["pixelsPerUnit"], System.Globalization.CultureInfo.InvariantCulture));
                changed = true;
            }

            string filterMode = GetString(args, "filterMode");
            if (!string.IsNullOrWhiteSpace(filterMode))
            {
                if (!Enum.TryParse(filterMode, true, out FilterMode parsedFilterMode))
                    throw new ArgumentException($"Unknown filterMode '{filterMode}'.");
                importer.filterMode = parsedFilterMode;
                changed = true;
            }

            if (args.ContainsKey("isReadable"))
            {
                importer.isReadable = Convert.ToBoolean(args["isReadable"]);
                changed = true;
            }

            string compression = GetString(args, "compression");
            if (!string.IsNullOrWhiteSpace(compression))
            {
                importer.textureCompression = compression.ToLowerInvariant() switch
                {
                    "none" or "uncompressed" => TextureImporterCompression.Uncompressed,
                    "low" or "lq" => TextureImporterCompression.CompressedLQ,
                    "normal" or "compressed" => TextureImporterCompression.Compressed,
                    "high" or "hq" => TextureImporterCompression.CompressedHQ,
                    _ => throw new ArgumentException($"Unknown compression '{compression}'.")
                };
                changed = true;
            }

            if (args.ContainsKey("alphaIsTransparency"))
            {
                importer.alphaIsTransparency = Convert.ToBoolean(args["alphaIsTransparency"]);
                changed = true;
            }

            string meshType = GetString(args, "meshType");
            if (!string.IsNullOrWhiteSpace(meshType))
            {
                if (!Enum.TryParse(meshType, true, out SpriteMeshType parsedMeshType))
                    throw new ArgumentException($"Unknown meshType '{meshType}'.");
                var serializedImporter = new SerializedObject(importer);
                var spriteMeshType = serializedImporter.FindProperty("m_SpriteMeshType");
                if (spriteMeshType == null)
                    throw new NotSupportedException("TextureImporter does not expose m_SpriteMeshType on this Unity version.");
                spriteMeshType.intValue = (int)parsedMeshType;
                serializedImporter.ApplyModifiedPropertiesWithoutUndo();
                changed = true;
            }

            if (args.ContainsKey("mipmapEnabled"))
            {
                importer.mipmapEnabled = Convert.ToBoolean(args["mipmapEnabled"]);
                changed = true;
            }

            if (changed)
                importer.SaveAndReimport();

            var importerObject = new SerializedObject(importer);
            var serializedMeshType = importerObject.FindProperty("m_SpriteMeshType");
            return new Dictionary<string, object>
            {
                { "type", importer.textureType.ToString() },
                { "spriteMode", importer.spriteImportMode.ToString() },
                { "pixelsPerUnit", importer.spritePixelsPerUnit },
                { "filterMode", importer.filterMode.ToString() },
                { "isReadable", importer.isReadable },
                { "compression", importer.textureCompression.ToString() },
                { "alphaIsTransparency", importer.alphaIsTransparency },
                { "meshType", serializedMeshType == null ? "" : ((SpriteMeshType)serializedMeshType.intValue).ToString() },
                { "mipmapEnabled", importer.mipmapEnabled }
            };
        }

        private static List<Dictionary<string, object>> DescribeSubAssets(string assetPath)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (asset == null) continue;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long fileID);
                result.Add(new Dictionary<string, object>
                {
                    { "name", asset.name }, { "type", asset.GetType().FullName },
                    { "guid", guid }, { "fileID", fileID }
                });
            }

            return result;
        }

        public static object Refresh(Dictionary<string, object> args)
        {
            bool forceUpdate = GetBool(args, "forceUpdate", true);
            bool saveAssets = GetBool(args, "saveAssets", false);
            bool reconcileExternalChanges = GetBool(args, "reconcileExternalChanges", true);
            var assetPaths = GetStringList(args, "assetPaths");

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

                // Targeted imports do not notice files deleted outside AssetDatabase.
                // Reconcile once after the ordered imports so Unity rebuilds its script
                // source list and removes stale compiler inputs such as deleted .cs files.
                if (reconcileExternalChanges)
                    AssetDatabase.Refresh(options | ImportAssetOptions.ForceSynchronousImport);
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
                { "reconciledExternalChanges", assetPaths.Count == 0 || reconcileExternalChanges },
                { "refreshedAllAssets", importedPaths.Count == 0 || reconcileExternalChanges },
                { "isUpdating", EditorApplication.isUpdating },
                { "isCompiling", EditorApplication.isCompiling },
            };
        }

        public static object ExportUnityPackage(Dictionary<string, object> args)
        {
            var assetPaths = GetStringList(args, "assetPaths");
            string outputPath = GetString(args, "outputPath");

            if (assetPaths.Count == 0)
                return new { error = "assetPaths is required" };
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
            string path = NormalizeAssetPath(GetString(args, "path"));
            string newName = GetString(args, "newName");
            bool dryRun = GetBool(args, "dryRun", false);

            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };
            if (string.IsNullOrEmpty(newName))
                return new { error = "newName is required" };
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

            bool synchronizedSingleSpriteName = SynchronizeSingleSpriteName(expectedPath, renameName);
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
                { "synchronizedSingleSpriteName", synchronizedSingleSpriteName },
                { "subAssets", DescribeSubAssets(newPath) },
            };
        }

        private static bool SynchronizeSingleSpriteName(string assetPath, string spriteName)
        {
            if (AssetImporter.GetAtPath(assetPath) is not TextureImporter importer ||
                importer.textureType != TextureImporterType.Sprite ||
                importer.spriteImportMode != SpriteImportMode.Single)
                return false;

            var serializedImporter = new SerializedObject(importer);
            var nameTable = serializedImporter.FindProperty("m_InternalIDToNameTable") ??
                            serializedImporter.FindProperty("internalIDToNameTable");
            if (nameTable != null && nameTable.isArray)
            {
                for (int i = 0; i < nameTable.arraySize; i++)
                {
                    var entry = nameTable.GetArrayElementAtIndex(i);
                    var name = entry.FindPropertyRelative("second") ?? entry.FindPropertyRelative("name");
                    if (name != null) name.stringValue = spriteName;
                }
                serializedImporter.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (var sprite in AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>())
            {
                sprite.name = spriteName;
                EditorUtility.SetDirty(sprite);
            }

            importer.SaveAndReimport();
            return AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Sprite>()
                .All(sprite => sprite.name == spriteName);
        }

        public static object Move(Dictionary<string, object> args)
        {
            if (!MCPExecutionOptions.TryParse(args, out var execution, out string executionError))
                return new { success = false, error = executionError };
            bool dryRun = GetBool(args, "dryRun", false);
            if (!TryPrepareMoves(args, out var entries, out object preparationError))
                return preparationError;

            if (dryRun)
                return BuildMoveResult(entries, execution, true, new List<string>());
            return ExecutePreparedMoves(entries, execution);
        }

        public static void MoveDeferred(Dictionary<string, object> args, Action<object> resolve,
            Action<object> progress)
        {
            if (!MCPExecutionOptions.TryParse(args, out var execution, out string executionError))
            {
                resolve(new { success = false, error = executionError });
                return;
            }
            if (!TryPrepareMoves(args, out var entries, out object preparationError))
            {
                resolve(preparationError);
                return;
            }
            if (GetBool(args, "dryRun", false))
            {
                resolve(BuildMoveResult(entries, execution, true, new List<string>()));
                return;
            }
            if (execution.ResolveMode(entries.Count) == MCPExecutionMode.Immediate)
            {
                resolve(ExecutePreparedMoves(entries, execution));
                return;
            }

            int nextIndex = 0;
            double startedAt = EditorApplication.timeSinceStartup;
            var errors = new List<string>();
            EditorApplication.CallbackFunction tick = null;
            Action<object> complete = result =>
            {
                if (tick != null)
                    EditorApplication.update -= tick;
                resolve(result);
            };
            tick = () =>
            {
                int elapsedMs = (int)((EditorApplication.timeSinceStartup - startedAt) * 1000d);
                if (elapsedMs >= execution.TimeoutMs)
                {
                    string timeoutError = $"Asset moves timed out after {execution.TimeoutMs} ms";
                    errors.Add(timeoutError);
                    var rollbackErrors = RollbackMoves(entries);
                    errors.AddRange(rollbackErrors);
                    FinishAssetMoves();
                    complete(BuildMoveFailure(entries, execution, timeoutError, errors));
                    return;
                }

                double frameStartedAt = EditorApplication.timeSinceStartup;
                int processedThisFrame = 0;
                AssetDatabase.StartAssetEditing();
                try
                {
                    while (nextIndex < entries.Count)
                    {
                        var entry = entries[nextIndex++];
                        string error = AssetDatabase.MoveAsset(entry.OldPath, entry.TargetPath);
                        if (string.IsNullOrEmpty(error))
                            entry.Moved = true;
                        else
                        {
                            entry.Error = error;
                            errors.Add($"Move {entry.Index} failed: {error}");
                            if (!execution.ContinueOnError)
                                break;
                        }

                        processedThisFrame++;
                        progress?.Invoke(BuildMoveProgress(entries, execution, nextIndex, elapsedMs));
                        double frameElapsedMs = (EditorApplication.timeSinceStartup - frameStartedAt) * 1000d;
                        if (processedThisFrame >= execution.OperationsPerFrame ||
                            frameElapsedMs >= execution.FrameBudgetMs)
                            break;
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception.Message);
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                if (errors.Count > 0 && !execution.ContinueOnError)
                {
                    errors.AddRange(RollbackMoves(entries));
                    FinishAssetMoves();
                    complete(BuildMoveFailure(entries, execution, errors[0], errors));
                    return;
                }
                if (nextIndex < entries.Count)
                    return;

                FinishAssetMoves();
                if (errors.Count > 0)
                    complete(BuildMoveFailure(entries, execution, "One or more asset moves failed", errors));
                else
                    complete(BuildMoveResult(entries, execution, false, errors));
            };
            EditorApplication.update += tick;
            tick();
        }

        private static bool TryPrepareMoves(Dictionary<string, object> args, out List<BatchMoveEntry> entries,
            out object errorResult)
        {
            entries = new List<BatchMoveEntry>();
            errorResult = null;
            List<Dictionary<string, object>> requestedMoves = GetDictionaryList(args, "moves");
            if (requestedMoves.Count == 0)
            {
                errorResult = new { error = "moves must contain at least one move request" };
                return false;
            }

            for (int index = 0; index < requestedMoves.Count; index++)
            {
                var request = requestedMoves[index];
                string path = NormalizeAssetPath(GetString(request, "path"));
                string destinationPath = NormalizeAssetPath(GetFirstString(request, "destinationPath",
                    "destinationFolder"));
                if (string.IsNullOrEmpty(path))
                    return FailMovePreparation(index, "path is required", out errorResult);
                if (string.IsNullOrEmpty(destinationPath))
                    return FailMovePreparation(index,
                        "destinationPath or destinationFolder is required", out errorResult);
                if (!AssetExists(path))
                    return FailMovePreparation(index, $"Asset not found at '{path}'", out errorResult);

                string targetPath = NormalizeMoveTargetPath(path, destinationPath);
                string targetDirectory = Path.GetDirectoryName(targetPath)?.Replace('\\', '/') ?? "";
                bool sourceIsFolder = AssetDatabase.IsValidFolder(path);
                if (!sourceIsFolder && !AssetDatabase.IsValidFolder(destinationPath))
                {
                    string sourceExtension = Path.GetExtension(path);
                    string targetExtension = Path.GetExtension(targetPath);
                    if (string.IsNullOrEmpty(targetExtension))
                        return FailMovePreparation(index,
                            "destinationPath must be an existing folder or include the asset file extension",
                            out errorResult);
                    if (!string.Equals(sourceExtension, targetExtension, StringComparison.OrdinalIgnoreCase))
                        return FailMovePreparation(index,
                            $"Changing file extension is not supported: '{sourceExtension}' to '{targetExtension}'",
                            out errorResult);
                }

                if (!string.IsNullOrEmpty(targetDirectory) && !AssetDatabase.IsValidFolder(targetDirectory))
                    return FailMovePreparation(index, $"Target directory does not exist: '{targetDirectory}'",
                        out errorResult);
                if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                    return FailMovePreparation(index, "Source and target paths are the same", out errorResult);
                if (AssetExists(targetPath))
                    return FailMovePreparation(index, $"Target asset already exists at '{targetPath}'", out errorResult);

                entries.Add(new BatchMoveEntry
                {
                    Index = index,
                    OldPath = path,
                    RequestedDestinationPath = destinationPath,
                    TargetPath = targetPath,
                    OldGuid = AssetDatabase.AssetPathToGUID(path),
                    OldMetaPath = GetMetaPath(path),
                    OldMetaExists = File.Exists(GetAbsolutePath(GetMetaPath(path))),
                });
            }

            for (int index = 0; index < entries.Count; index++)
            {
                for (int otherIndex = index + 1; otherIndex < entries.Count; otherIndex++)
                {
                    if (string.Equals(entries[index].OldPath, entries[otherIndex].OldPath,
                            StringComparison.OrdinalIgnoreCase))
                        return FailMovePreparation(otherIndex,
                            $"Duplicate source path '{entries[otherIndex].OldPath}'", out errorResult);
                    if (string.Equals(entries[index].TargetPath, entries[otherIndex].TargetPath,
                            StringComparison.OrdinalIgnoreCase))
                        return FailMovePreparation(otherIndex,
                            $"Duplicate target path '{entries[otherIndex].TargetPath}'", out errorResult);
                }
            }

            return true;
        }

        private static bool FailMovePreparation(int index, string error, out object errorResult)
        {
            errorResult = BatchMoveValidationError(index, error);
            return false;
        }

        private static object ExecutePreparedMoves(List<BatchMoveEntry> entries, MCPExecutionOptions execution)
        {
            var errors = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var entry in entries)
                {
                    string error = AssetDatabase.MoveAsset(entry.OldPath, entry.TargetPath);
                    if (string.IsNullOrEmpty(error))
                    {
                        entry.Moved = true;
                        continue;
                    }

                    entry.Error = error;
                    errors.Add($"Move {entry.Index} failed: {error}");
                    if (!execution.ContinueOnError)
                        break;
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (errors.Count > 0 && !execution.ContinueOnError)
                errors.AddRange(RollbackMoves(entries));
            FinishAssetMoves();
            return errors.Count == 0
                ? BuildMoveResult(entries, execution, false, errors)
                : BuildMoveFailure(entries, execution, errors[0], errors);
        }

        private static List<string> RollbackMoves(List<BatchMoveEntry> entries)
        {
            var rollbackErrors = new List<string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int index = entries.Count - 1; index >= 0; index--)
                {
                    var entry = entries[index];
                    if (!entry.Moved || entry.RolledBack)
                        continue;
                    string error = AssetDatabase.MoveAsset(entry.TargetPath, entry.OldPath);
                    if (string.IsNullOrEmpty(error))
                        entry.RolledBack = true;
                    else
                        rollbackErrors.Add($"Rollback {entry.Index} failed: {error}");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return rollbackErrors;
        }

        private static void FinishAssetMoves()
        {
            AssetDatabase.SaveAssets();
        }

        private static Dictionary<string, object> BuildMoveResult(List<BatchMoveEntry> entries,
            MCPExecutionOptions execution, bool dryRun, List<string> errors)
        {
            return new Dictionary<string, object>
            {
                { "success", errors.Count == 0 },
                { "dryRun", dryRun },
                { "moveCount", entries.Count },
                { "movedCount", entries.FindAll(entry => entry.Moved && !entry.RolledBack).Count },
                { "failedCount", entries.FindAll(entry => !string.IsNullOrEmpty(entry.Error)).Count },
                { "moves", entries.ConvertAll(CreateBatchMoveResult) },
                { "execution", execution.ToResult(entries.Count) },
            };
        }

        private static Dictionary<string, object> BuildMoveFailure(List<BatchMoveEntry> entries,
            MCPExecutionOptions execution, string error, List<string> errors)
        {
            var result = BuildMoveResult(entries, execution, false, errors);
            result["success"] = false;
            result["error"] = error;
            result["errors"] = errors;
            result["rolledBack"] = entries.TrueForAll(entry => !entry.Moved || entry.RolledBack);
            return result;
        }

        private static Dictionary<string, object> BuildMoveProgress(List<BatchMoveEntry> entries,
            MCPExecutionOptions execution, int nextIndex, int elapsedMs)
        {
            return new Dictionary<string, object>
            {
                { "phase", "moving" },
                { "moveCount", entries.Count },
                { "processedCount", nextIndex },
                { "elapsedMs", elapsedMs },
                { "execution", execution.ToResult(entries.Count) },
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

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "name", instance.name },
                { "instanceId", MCPObjectId.Get(instance) },
            };
            MCPTransformSerialization.AddWorld(result, instance.transform);
            return result;
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
                { "error", entry.Error ?? "" },
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
            public string Error;
        }
    }
}
