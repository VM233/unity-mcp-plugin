using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace UnityMCP.Editor
{
    public static class MCPAssetWorkspaceCommands
    {
        private const int DefaultPageSize = 100;
        private const int MaxPageSize = 500;

        public static object EnsureFolder(Dictionary<string, object> args)
        {
            string path = NormalizeAssetPath(GetString(args, "path"));
            bool dryRun = GetBool(args, "dryRun", false);
            if (!TryValidateFolderPath(path, out string error))
                return MCPResponse.Error(error, "invalid_folder_path");

            bool existed = AssetDatabase.IsValidFolder(path);
            var created = new List<string>();
            if (!existed && !dryRun)
                EnsureFolderPath(path, created);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "existed", existed },
                { "created", created },
                { "dryRun", dryRun },
            };
        }

        public static object Copy(Dictionary<string, object> args)
        {
            var requests = GetDictionaryList(args, "copies");
            if (requests.Count == 0)
                requests.Add(args ?? new Dictionary<string, object>());

            bool dryRun = GetBool(args, "dryRun", false);
            bool overwrite = GetBool(args, "overwrite", false);
            var prepared = new List<CopyRequest>();
            var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                string source = NormalizeAssetPath(GetString(request, "sourcePath"));
                string target = NormalizeAssetPath(GetString(request, "targetPath"));
                if (string.IsNullOrEmpty(source) || AssetDatabase.LoadMainAssetAtPath(source) == null)
                    return MCPResponse.Error($"Source asset was not found: '{source}'", "asset_not_found");
                if (!TryValidateAssetPath(target, out string targetError))
                    return MCPResponse.Error(targetError, "invalid_asset_path");
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                    return MCPResponse.Error("sourcePath and targetPath must be different.", "invalid_asset_path");
                if (!targetPaths.Add(target))
                    return MCPResponse.Error($"Duplicate targetPath in copy batch: '{target}'.",
                        "duplicate_target_path");
                if (AssetDatabase.IsValidFolder(source))
                    return MCPResponse.Error("Generic asset copy currently accepts files, not folders.", "folder_copy_not_supported");
                if (AssetDatabase.LoadMainAssetAtPath(target) != null && !overwrite)
                    return MCPResponse.Error($"Target asset already exists: '{target}'", "asset_exists");
                prepared.Add(new CopyRequest { SourcePath = source, TargetPath = target });
            }

            if (dryRun)
                return BuildCopyResult(prepared, true, false, new List<string>());

            var snapshots = new List<FileSnapshot>();
            var created = new List<string>();
            var errors = new List<string>();
            try
            {
                foreach (var request in prepared)
                {
                    EnsureParentFolder(request.TargetPath, created);
                    if (AssetDatabase.LoadMainAssetAtPath(request.TargetPath) != null)
                    {
                        snapshots.Add(CaptureFileSnapshot(request.TargetPath));
                        if (!AssetDatabase.DeleteAsset(request.TargetPath))
                            throw new InvalidOperationException($"Failed to replace '{request.TargetPath}'.");
                    }

                    if (!AssetDatabase.CopyAsset(request.SourcePath, request.TargetPath))
                        throw new InvalidOperationException(
                            $"AssetDatabase.CopyAsset failed: '{request.SourcePath}' -> '{request.TargetPath}'.");
                    request.Copied = true;
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return BuildCopyResult(prepared, false, false, errors);
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
                for (int index = prepared.Count - 1; index >= 0; index--)
                {
                    if (prepared[index].Copied)
                        AssetDatabase.DeleteAsset(prepared[index].TargetPath);
                }
                RestoreSnapshots(snapshots, errors);
                DeleteCreatedFolders(created, errors);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return BuildCopyResult(prepared, false, true, errors);
            }
        }

        public static object Dependencies(Dictionary<string, object> args)
        {
            string path = NormalizeAssetPath(GetString(args, "path"));
            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadMainAssetAtPath(path) == null)
                return MCPResponse.Error($"Asset was not found: '{path}'", "asset_not_found");

            string direction = (GetString(args, "direction") ?? "both").ToLowerInvariant();
            if (direction != "outgoing" && direction != "incoming" && direction != "both")
                return MCPResponse.Error("direction must be outgoing, incoming, or both.", "invalid_arguments");

            bool recursive = GetBool(args, "recursive", true);
            int offset = Math.Max(0, GetInt(args, "offset", 0));
            int limit = Math.Max(1, Math.Min(MaxPageSize, GetInt(args, "limit", DefaultPageSize)));
            var searchRoots = GetStringList(args, "searchRoots");
            if (searchRoots.Count == 0)
                searchRoots.Add("Assets");
            searchRoots = searchRoots.Select(NormalizeAssetPath)
                .Where(AssetDatabase.IsValidFolder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var outgoing = new List<string>();
            if (direction != "incoming")
            {
                outgoing.AddRange(AssetDatabase.GetDependencies(path, recursive)
                    .Select(NormalizeAssetPath)
                    .Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase)));
            }

            var incoming = new List<string>();
            if (direction != "outgoing")
            {
                foreach (string guid in AssetDatabase.FindAssets("", searchRoots.ToArray()))
                {
                    string candidate = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                    if (string.IsNullOrEmpty(candidate) || candidate == path || AssetDatabase.IsValidFolder(candidate))
                        continue;
                    if (AssetDatabase.GetDependencies(candidate, recursive)
                        .Any(dependency => string.Equals(NormalizeAssetPath(dependency), path,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        incoming.Add(candidate);
                    }
                }
            }

            outgoing = outgoing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
            incoming = incoming.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item).ToList();
            var combined = outgoing.Select(item => DescribeReference(item, "outgoing"))
                .Concat(incoming.Select(item => DescribeReference(item, "incoming")))
                .OrderBy(item => item["path"].ToString(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item["direction"].ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();
            var page = combined.Skip(offset).Take(limit).ToList();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "direction", direction },
                { "recursive", recursive },
                { "outgoingCount", outgoing.Count },
                { "incomingCount", incoming.Count },
                { "total", combined.Count },
                { "offset", offset },
                { "limit", limit },
                { "hasMore", offset + page.Count < combined.Count },
                { "nextOffset", offset + page.Count < combined.Count ? (object)(offset + page.Count) : null },
                { "references", page },
            };
        }

        public static object Transaction(Dictionary<string, object> args)
        {
            var operations = GetDictionaryList(args, "operations");
            if (operations.Count == 0)
                return MCPResponse.Error("operations is required.", "invalid_arguments");

            bool dryRun = GetBool(args, "dryRun", false);
            string transactionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var prepared = new List<Dictionary<string, object>>();
            var virtualCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var virtualRemoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var virtualFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var operation in operations)
            {
                string type = (GetString(operation, "type") ?? "").Trim().ToLowerInvariant();
                if (type != "ensure-folder" && type != "copy" && type != "move" &&
                    type != "delete" && type != "serialized-set")
                {
                    return MCPResponse.Error($"Unsupported transaction operation '{type}'.", "invalid_operation");
                }
                var normalized = new Dictionary<string, object>(operation) { ["type"] = type };
                NormalizeOperationPaths(normalized);
                if (!TryPreflightOperation(normalized, virtualCreated, virtualRemoved, virtualFolders,
                        out string error))
                    return MCPResponse.Error(error, "transaction_preflight_failed");
                prepared.Add(normalized);
                ApplyVirtualOperation(normalized, virtualCreated, virtualRemoved, virtualFolders);
            }

            if (dryRun)
            {
                return new Dictionary<string, object>
                {
                    { "success", true }, { "transactionId", transactionId }, { "dryRun", true },
                    { "operationCount", prepared.Count }, { "operations", prepared },
                };
            }

            var rollback = new Stack<Action>();
            var rollbackErrors = new List<string>();
            var results = new List<object>();
            try
            {
                foreach (var operation in prepared)
                    results.Add(ExecuteTransactionOperation(operation, rollback));

                VerifyTransactionPostconditions(args);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return new Dictionary<string, object>
                {
                    { "success", true }, { "transactionId", transactionId }, { "dryRun", false },
                    { "operationCount", prepared.Count }, { "results", results }, { "rolledBack", false },
                };
            }
            catch (Exception exception)
            {
                while (rollback.Count > 0)
                {
                    try { rollback.Pop().Invoke(); }
                    catch (Exception rollbackException) { rollbackErrors.Add(rollbackException.Message); }
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return MCPResponse.Error(exception.Message, "asset_transaction_failed", false,
                    new Dictionary<string, object>
                    {
                        { "transactionId", transactionId }, { "rolledBack", rollbackErrors.Count == 0 },
                        { "rollbackErrors", rollbackErrors }, { "completedOperationCount", results.Count },
                        { "results", results },
                    });
            }
        }

        private static object ExecuteTransactionOperation(Dictionary<string, object> operation,
            Stack<Action> rollback)
        {
            string type = GetString(operation, "type");
            switch (type)
            {
                case "ensure-folder":
                {
                    string path = GetString(operation, "path");
                    var created = new List<string>();
                    EnsureFolderPath(path, created);
                    rollback.Push(() => DeleteCreatedFolders(created, new List<string>()));
                    return new { type, path, created };
                }
                case "copy":
                {
                    string sourcePath = GetString(operation, "sourcePath");
                    string targetPath = GetString(operation, "targetPath");
                    var createdFolders = new List<string>();
                    EnsureParentFolder(targetPath, createdFolders);
                    FileSnapshot replaced = AssetDatabase.LoadMainAssetAtPath(targetPath) != null
                        ? CaptureFileSnapshot(targetPath)
                        : null;
                    if (replaced != null)
                        AssetDatabase.DeleteAsset(targetPath);
                    if (!AssetDatabase.CopyAsset(sourcePath, targetPath))
                        throw new InvalidOperationException($"Copy failed: '{sourcePath}' -> '{targetPath}'.");
                    rollback.Push(() =>
                    {
                        AssetDatabase.DeleteAsset(targetPath);
                        if (replaced != null) RestoreSnapshot(replaced);
                        DeleteCreatedFolders(createdFolders, new List<string>());
                    });
                    return new { type, sourcePath, targetPath };
                }
                case "move":
                {
                    string sourcePath = GetString(operation, "sourcePath");
                    string targetPath = GetString(operation, "targetPath");
                    var createdFolders = new List<string>();
                    EnsureParentFolder(targetPath, createdFolders);
                    string error = AssetDatabase.MoveAsset(sourcePath, targetPath);
                    if (!string.IsNullOrEmpty(error))
                        throw new InvalidOperationException(error);
                    rollback.Push(() =>
                    {
                        string moveBackError = AssetDatabase.MoveAsset(targetPath, sourcePath);
                        if (!string.IsNullOrEmpty(moveBackError))
                            throw new InvalidOperationException(moveBackError);
                        DeleteCreatedFolders(createdFolders, new List<string>());
                    });
                    return new { type, sourcePath, targetPath };
                }
                case "delete":
                {
                    string path = GetString(operation, "path");
                    FileSnapshot snapshot = CaptureFileSnapshot(path);
                    if (!AssetDatabase.DeleteAsset(path))
                        throw new InvalidOperationException($"Delete failed: '{path}'.");
                    rollback.Push(() => RestoreSnapshot(snapshot));
                    return new { type, path };
                }
                case "serialized-set":
                {
                    string assetPath = GetString(operation, "assetPath");
                    FileSnapshot snapshot = CaptureFileSnapshot(assetPath);
                    object result = MCPSerializedObjectCommands.Set(operation);
                    if (MCPResponse.TryGetError(result, out string message, out _, out _))
                        throw new InvalidOperationException(message);
                    rollback.Push(() => RestoreSnapshot(snapshot));
                    return result;
                }
                default:
                    throw new InvalidOperationException($"Unsupported transaction operation '{type}'.");
            }
        }

        private static void VerifyTransactionPostconditions(Dictionary<string, object> args)
        {
            foreach (string path in GetStringList(args, "requiredAssets"))
            {
                string normalized = NormalizeAssetPath(path);
                if (AssetDatabase.LoadMainAssetAtPath(normalized) == null && !AssetDatabase.IsValidFolder(normalized))
                    throw new InvalidOperationException($"Required asset was not found after transaction: '{normalized}'.");
            }

            foreach (var check in GetDictionaryList(args, "referenceChecks"))
            {
                string assetPath = NormalizeAssetPath(GetString(check, "assetPath"));
                var dependencies = new HashSet<string>(AssetDatabase.GetDependencies(assetPath, true)
                    .Select(NormalizeAssetPath), StringComparer.OrdinalIgnoreCase);
                foreach (string required in GetStringList(check, "requiredDependencies"))
                {
                    string normalized = NormalizeAssetPath(required);
                    if (!dependencies.Contains(normalized))
                        throw new InvalidOperationException(
                            $"'{assetPath}' does not reference required dependency '{normalized}'.");
                }
            }
        }

        private static bool TryPreflightOperation(Dictionary<string, object> operation,
            HashSet<string> virtualCreated, HashSet<string> virtualRemoved, HashSet<string> virtualFolders,
            out string error)
        {
            error = null;
            string type = GetString(operation, "type");
            if (type == "ensure-folder")
                return TryValidateFolderPath(GetString(operation, "path"), out error);
            if (type == "copy" || type == "move")
            {
                string source = GetString(operation, "sourcePath");
                string target = GetString(operation, "targetPath");
                if (!VirtuallyExists(source, virtualCreated, virtualRemoved))
                {
                    error = $"Source asset was not found: '{source}'.";
                    return false;
                }
                if (!TryValidateAssetPath(target, out error)) return false;
                if (VirtuallyExists(target, virtualCreated, virtualRemoved))
                {
                    error = $"Target already exists: '{target}'.";
                    return false;
                }
                if (type == "copy" && (virtualFolders.Contains(source) ||
                                       (!virtualRemoved.Contains(source) && AssetDatabase.IsValidFolder(source))))
                {
                    error = "Transaction copy currently accepts files, not folders.";
                    return false;
                }
                return true;
            }
            if (type == "delete")
            {
                string path = GetString(operation, "path");
                if (virtualFolders.Contains(path) ||
                    (!virtualRemoved.Contains(path) && AssetDatabase.IsValidFolder(path)))
                {
                    error = "Transaction delete currently accepts files, not folders.";
                    return false;
                }
                if (!VirtuallyExists(path, virtualCreated, virtualRemoved))
                {
                    error = $"Asset was not found: '{path}'.";
                    return false;
                }
                return true;
            }
            if (type == "serialized-set")
            {
                string path = GetString(operation, "assetPath");
                if (!VirtuallyExists(path, virtualCreated, virtualRemoved))
                {
                    error = $"Serialized asset was not found: '{path}'.";
                    return false;
                }
                return true;
            }
            return false;
        }

        private static bool VirtuallyExists(string path, HashSet<string> created, HashSet<string> removed)
        {
            if (created.Contains(path)) return true;
            if (removed.Contains(path)) return false;
            return AssetDatabase.LoadMainAssetAtPath(path) != null || AssetDatabase.IsValidFolder(path);
        }

        private static void ApplyVirtualOperation(Dictionary<string, object> operation,
            HashSet<string> created, HashSet<string> removed, HashSet<string> folders)
        {
            string type = GetString(operation, "type");
            if (type == "ensure-folder")
            {
                string path = GetString(operation, "path");
                removed.Remove(path);
                created.Add(path);
                folders.Add(path);
                return;
            }
            if (type == "copy")
            {
                string target = GetString(operation, "targetPath");
                removed.Remove(target);
                created.Add(target);
                folders.Remove(target);
                return;
            }
            if (type == "move")
            {
                string source = GetString(operation, "sourcePath");
                string target = GetString(operation, "targetPath");
                bool sourceIsFolder = folders.Contains(source) ||
                                      (!removed.Contains(source) && AssetDatabase.IsValidFolder(source));
                created.Remove(source);
                removed.Add(source);
                folders.Remove(source);
                removed.Remove(target);
                created.Add(target);
                if (sourceIsFolder) folders.Add(target); else folders.Remove(target);
                return;
            }
            if (type == "delete")
            {
                string path = GetString(operation, "path");
                created.Remove(path);
                removed.Add(path);
                folders.Remove(path);
            }
        }

        private static void NormalizeOperationPaths(Dictionary<string, object> operation)
        {
            foreach (string key in new[] { "path", "sourcePath", "targetPath", "assetPath" })
            {
                if (operation.TryGetValue(key, out object value) && value != null)
                    operation[key] = NormalizeAssetPath(value.ToString());
            }
        }

        private static Dictionary<string, object> DescribeReference(string path, string direction)
        {
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new Dictionary<string, object>
            {
                { "path", path }, { "direction", direction },
                { "guid", AssetDatabase.AssetPathToGUID(path) }, { "type", type?.Name ?? "Unknown" },
            };
        }

        private static object BuildCopyResult(List<CopyRequest> requests, bool dryRun, bool rolledBack,
            List<string> errors)
        {
            return new Dictionary<string, object>
            {
                { "success", errors.Count == 0 }, { "dryRun", dryRun }, { "rolledBack", rolledBack },
                { "copyCount", requests.Count }, { "copiedCount", requests.Count(item => item.Copied) },
                { "copies", requests.Select(item => new Dictionary<string, object>
                    {
                        { "sourcePath", item.SourcePath }, { "targetPath", item.TargetPath },
                        { "copied", item.Copied },
                    }).ToList() },
                { "errors", errors },
            };
        }

        private static FileSnapshot CaptureFileSnapshot(string assetPath)
        {
            string absolutePath = ToAbsolutePath(assetPath);
            if (!File.Exists(absolutePath))
                throw new InvalidOperationException($"Asset file was not found on disk: '{assetPath}'.");
            string metaPath = absolutePath + ".meta";
            return new FileSnapshot
            {
                AssetPath = assetPath,
                AssetBytes = File.ReadAllBytes(absolutePath),
                MetaBytes = File.Exists(metaPath) ? File.ReadAllBytes(metaPath) : null,
            };
        }

        private static void RestoreSnapshots(IEnumerable<FileSnapshot> snapshots, List<string> errors)
        {
            foreach (var snapshot in snapshots.Reverse())
            {
                try { RestoreSnapshot(snapshot); }
                catch (Exception exception) { errors.Add(exception.Message); }
            }
        }

        private static void RestoreSnapshot(FileSnapshot snapshot)
        {
            string absolutePath = ToAbsolutePath(snapshot.AssetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.WriteAllBytes(absolutePath, snapshot.AssetBytes);
            if (snapshot.MetaBytes != null)
                File.WriteAllBytes(absolutePath + ".meta", snapshot.MetaBytes);
            AssetDatabase.ImportAsset(snapshot.AssetPath, ImportAssetOptions.ForceSynchronousImport |
                                                          ImportAssetOptions.ForceUpdate);
        }

        private static void EnsureParentFolder(string assetPath, List<string> created)
        {
            string parent = NormalizeAssetPath(Path.GetDirectoryName(assetPath));
            if (!string.IsNullOrEmpty(parent)) EnsureFolderPath(parent, created);
        }

        private static void EnsureFolderPath(string path, List<string> created)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int index = 1; index < parts.Length; index++)
            {
                string next = current + "/" + parts[index];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    string guid = AssetDatabase.CreateFolder(current, parts[index]);
                    if (string.IsNullOrEmpty(guid))
                        throw new InvalidOperationException($"Failed to create folder '{next}'.");
                    created.Add(next);
                }
                current = next;
            }
        }

        private static void DeleteCreatedFolders(List<string> created, List<string> errors)
        {
            for (int index = created.Count - 1; index >= 0; index--)
            {
                if (!AssetDatabase.IsValidFolder(created[index])) continue;
                if (!AssetDatabase.DeleteAsset(created[index]))
                    errors.Add($"Failed to remove created folder '{created[index]}'.");
            }
        }

        private static bool TryValidateFolderPath(string path, out string error)
        {
            if (string.IsNullOrEmpty(path) || (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal)))
            {
                error = "Folder path must be Assets or a child of Assets.";
                return false;
            }
            error = null;
            return true;
        }

        private static bool TryValidateAssetPath(string path, out string error)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                error = "Asset path must point to a file below Assets/.";
                return false;
            }
            error = null;
            return true;
        }

        private static string ToAbsolutePath(string assetPath)
        {
            string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath).FullName;
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string NormalizeAssetPath(string path)
        {
            return (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : null;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null) return defaultValue;
            return value is bool boolValue ? boolValue : bool.TryParse(value.ToString(), out bool parsed) && parsed;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null) return defaultValue;
            return int.TryParse(value.ToString(), out int parsed) ? parsed : defaultValue;
        }

        private static List<string> GetStringList(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || !(value is IList list))
                return new List<string>();
            return list.Cast<object>().Where(item => item != null).Select(item => item.ToString()).ToList();
        }

        private static List<Dictionary<string, object>> GetDictionaryList(Dictionary<string, object> args,
            string key)
        {
            if (args == null || !args.TryGetValue(key, out object value) || !(value is IList list))
                return new List<Dictionary<string, object>>();
            return list.Cast<object>().Select(MCPResponse.ToDictionary).Where(item => item != null).ToList();
        }

        private sealed class CopyRequest
        {
            public string SourcePath;
            public string TargetPath;
            public bool Copied;
        }

        private sealed class FileSnapshot
        {
            public string AssetPath;
            public byte[] AssetBytes;
            public byte[] MetaBytes;
        }
    }
}
