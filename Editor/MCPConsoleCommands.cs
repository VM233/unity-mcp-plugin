using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityMCP.Editor
{
    [InitializeOnLoad]
    public static class MCPConsoleCommands
    {
        // Store log messages via Application.logMessageReceivedThreaded
        private static readonly List<LogEntry> _logEntries = new List<LogEntry>();
        private static bool _isListening = false;
        private static bool _playModeHooked = false;
        private static DateTime _lastPlayStartedAt = DateTime.MinValue;
        private const int MaxEntries = 1000;

        private struct LogEntry
        {
            public string message;
            public string stackTrace;
            public LogType type;
            public DateTime timestamp;
        }

        // ─── Compilation error buffer (independent of console log) ───
        // Populated via CompilationPipeline.assemblyCompilationFinished.
        // Cleared automatically at the start of each new compilation cycle.
        // Not affected by console Clear().
        private static readonly List<CompilationError> _compilationErrors = new List<CompilationError>();
        private static bool _compilationHooked = false;

        private struct CompilationError
        {
            public string file;
            public int line;
            public int column;
            public string message;
            public string severity; // "error" or "warning"
            public string assembly;
            public DateTime timestamp;
        }

        // Static constructor — runs at editor load thanks to [InitializeOnLoad]
        static MCPConsoleCommands()
        {
            EnsureListening();
            EnsureCompilationHook();
            EnsurePlayModeHook();
        }

        /// <summary>
        /// Start capturing console messages. Safe to call multiple times.
        /// Called automatically at editor load AND when the bridge server starts.
        /// </summary>
        public static void EnsureListening()
        {
            if (_isListening) return;
            // Use logMessageReceivedThreaded to capture messages from ALL threads,
            // not just the main thread. This catches async compilation errors,
            // background job failures, etc.
            Application.logMessageReceivedThreaded += OnLogMessage;
            _isListening = true;
        }

        public static void EnsurePlayModeHook()
        {
            if (_playModeHooked) return;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (EditorApplication.isPlaying && _lastPlayStartedAt == DateTime.MinValue)
                _lastPlayStartedAt = DateTime.Now;
            _playModeHooked = true;
        }

        /// <summary>
        /// Hook into CompilationPipeline to capture compiler messages (errors/warnings)
        /// independently from the console log buffer. Safe to call multiple times.
        /// </summary>
        public static void EnsureCompilationHook()
        {
            if (_compilationHooked) return;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            _compilationHooked = true;
        }

        private static void OnCompilationStarted(object context)
        {
            // Fresh compilation cycle — clear previous results
            lock (_compilationErrors) { _compilationErrors.Clear(); }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.EnteredPlayMode)
                _lastPlayStartedAt = DateTime.Now;
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            // Extract assembly name from path (e.g. "Library/ScriptAssemblies/Assembly-CSharp.dll" → "Assembly-CSharp")
            string asmName = System.IO.Path.GetFileNameWithoutExtension(assemblyPath);

            lock (_compilationErrors)
            {
                foreach (var msg in messages)
                {
                    // Only capture errors and warnings, skip info
                    if (msg.type != CompilerMessageType.Error && msg.type != CompilerMessageType.Warning)
                        continue;

                    _compilationErrors.Add(new CompilationError
                    {
                        file = msg.file ?? "",
                        line = msg.line,
                        column = msg.column,
                        message = msg.message ?? "",
                        severity = msg.type == CompilerMessageType.Error ? "error" : "warning",
                        assembly = asmName,
                        timestamp = DateTime.Now,
                    });
                }
            }
        }

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logEntries)
            {
                _logEntries.Add(new LogEntry
                {
                    message = message,
                    stackTrace = stackTrace,
                    type = type,
                    timestamp = DateTime.Now,
                });

                // Keep max entries capped
                if (_logEntries.Count > MaxEntries)
                    _logEntries.RemoveRange(0, _logEntries.Count - MaxEntries);
            }
        }

        public static object GetLog(Dictionary<string, object> args)
        {
            EnsureListening();

            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
            string typeFilter = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "all";

            var entries = new List<Dictionary<string, object>>();
            lock (_logEntries)
            {
                // Walk backwards through all entries, collecting matches until we have enough.
                // This ensures we get the most recent N entries that match the filter,
                // rather than filtering only the last N entries (which missed most errors).
                for (int i = _logEntries.Count - 1; i >= 0 && entries.Count < count; i--)
                {
                    var entry = _logEntries[i];

                    if (typeFilter != "all")
                    {
                        if (typeFilter == "error" && entry.type != LogType.Error && entry.type != LogType.Exception && entry.type != LogType.Assert)
                            continue;
                        if (typeFilter == "warning" && entry.type != LogType.Warning)
                            continue;
                        if (typeFilter == "info" && entry.type != LogType.Log)
                            continue;
                    }

                    entries.Add(new Dictionary<string, object>
                    {
                        { "message", entry.message },
                        { "type", entry.type.ToString().ToLower() },
                        { "timestamp", entry.timestamp.ToString("HH:mm:ss.fff") },
                        { "stackTrace", entry.stackTrace ?? "" },
                    });
                }
            }

            // Reverse so entries are in chronological order (oldest first)
            entries.Reverse();

            return new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "entries", entries },
            };
        }

        public static object Query(Dictionary<string, object> args)
        {
            EnsureListening();
            EnsurePlayModeHook();

            int count = GetInt(args, "count", 50);
            string typeFilter = GetString(args, "type", "all").ToLowerInvariant();
            string messageContains = GetString(args, "messageContains", "");
            string sourceContains = GetString(args, "sourceContains", "");
            string stackContains = GetString(args, "stackContains", "");
            bool includeStack = GetBool(args, "includeStack", true);
            bool sinceLastPlay = GetBool(args, "sinceLastPlay", false);
            bool newestFirst = GetBool(args, "newestFirst", false);

            DateTime? since = GetDateTime(args, "since");
            DateTime? until = GetDateTime(args, "until");
            double secondsAgo = GetDouble(args, "sinceSecondsAgo", -1d);
            if (secondsAgo >= 0d)
                since = MaxDateTime(since, DateTime.Now.AddSeconds(-secondsAgo));
            if (sinceLastPlay && _lastPlayStartedAt != DateTime.MinValue)
                since = MaxDateTime(since, _lastPlayStartedAt);

            var entries = new List<Dictionary<string, object>>();
            lock (_logEntries)
            {
                for (int i = _logEntries.Count - 1; i >= 0 && entries.Count < count; i--)
                {
                    var entry = _logEntries[i];
                    if (!MatchesLogType(entry.type, typeFilter))
                        continue;
                    if (since.HasValue && entry.timestamp < since.Value)
                        continue;
                    if (until.HasValue && entry.timestamp > until.Value)
                        continue;
                    if (!ContainsIgnoreCase(entry.message, messageContains))
                        continue;
                    if (!ContainsIgnoreCase(entry.stackTrace, stackContains))
                        continue;

                    string source = ExtractSource(entry.stackTrace);
                    if (!ContainsIgnoreCase(source, sourceContains))
                        continue;

                    var result = new Dictionary<string, object>
                    {
                        { "message", entry.message },
                        { "type", entry.type.ToString().ToLowerInvariant() },
                        { "timestamp", entry.timestamp.ToString("o") },
                        { "source", source },
                    };

                    if (includeStack)
                        result["stackTrace"] = entry.stackTrace ?? "";

                    entries.Add(result);
                }
            }

            if (!newestFirst)
                entries.Reverse();

            return new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "entries", entries },
                { "lastPlayStartedAt", _lastPlayStartedAt == DateTime.MinValue ? "" : _lastPlayStartedAt.ToString("o") },
                { "sinceLastPlay", sinceLastPlay },
            };
        }

        /// <summary>
        /// Get compilation errors/warnings captured via CompilationPipeline.
        /// Independent of the console log buffer — not affected by Clear().
        /// </summary>
        public static object GetCompilationErrors(Dictionary<string, object> args)
        {
            EnsureCompilationHook();

            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 50;
            string severityFilter = args.ContainsKey("severity") ? args["severity"].ToString().ToLower() : "all";

            var entries = new List<Dictionary<string, object>>();
            lock (_compilationErrors)
            {
                // Walk backwards to get most recent first, then reverse for chronological order
                for (int i = _compilationErrors.Count - 1; i >= 0 && entries.Count < count; i--)
                {
                    var err = _compilationErrors[i];

                    if (severityFilter != "all" && err.severity != severityFilter)
                        continue;

                    entries.Add(new Dictionary<string, object>
                    {
                        { "file", err.file },
                        { "line", err.line },
                        { "column", err.column },
                        { "message", err.message },
                        { "severity", err.severity },
                        { "assembly", err.assembly },
                        { "timestamp", err.timestamp.ToString("HH:mm:ss.fff") },
                    });
                }
            }

            entries.Reverse();

            return new Dictionary<string, object>
            {
                { "count", entries.Count },
                { "isCompiling", EditorApplication.isCompiling },
                { "entries", entries },
            };
        }

        public static object Clear()
        {
            EnsureListening();
            lock (_logEntries) { _logEntries.Clear(); }
            return new { success = true, message = "Console log buffer cleared" };
        }

        private static bool MatchesLogType(LogType type, string typeFilter)
        {
            if (string.IsNullOrEmpty(typeFilter) || typeFilter == "all")
                return true;
            if (typeFilter == "error")
                return type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            if (typeFilter == "warning")
                return type == LogType.Warning;
            if (typeFilter == "info")
                return type == LogType.Log;
            if (typeFilter == "exception")
                return type == LogType.Exception;
            if (typeFilter == "assert")
                return type == LogType.Assert;

            return string.Equals(type.ToString(), typeFilter, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractSource(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return "";

            var lines = stackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                int atIndex = line.IndexOf(" (at ", StringComparison.Ordinal);
                if (atIndex >= 0)
                    return line.Substring(atIndex + 5).TrimEnd(')');
            }

            return lines.Length > 0 ? lines[0].Trim() : "";
        }

        private static bool ContainsIgnoreCase(string value, string filter)
        {
            return string.IsNullOrEmpty(filter) ||
                   (value != null && value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetString(Dictionary<string, object> args, string key, string defaultValue)
        {
            return args != null && args.ContainsKey(key) && args[key] != null
                ? args[key].ToString()
                : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;
            if (args[key] is bool value)
                return value;

            return bool.TryParse(args[key].ToString(), out bool parsed) ? parsed : defaultValue;
        }

        private static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            return int.TryParse(args[key].ToString(), out int value) ? value : defaultValue;
        }

        private static double GetDouble(Dictionary<string, object> args, string key, double defaultValue)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return defaultValue;

            return double.TryParse(args[key].ToString(), out double value) ? value : defaultValue;
        }

        private static DateTime? GetDateTime(Dictionary<string, object> args, string key)
        {
            if (args == null || !args.ContainsKey(key) || args[key] == null)
                return null;

            string value = args[key].ToString();
            if (double.TryParse(value, out double numeric))
            {
                long unixValue = Convert.ToInt64(numeric);
                if (unixValue > 100000000000)
                    return DateTimeOffset.FromUnixTimeMilliseconds(unixValue).LocalDateTime;
                if (unixValue > 1000000000)
                    return DateTimeOffset.FromUnixTimeSeconds(unixValue).LocalDateTime;
            }

            return DateTime.TryParse(value, out DateTime parsed) ? parsed : null;
        }

        private static DateTime? MaxDateTime(DateTime? current, DateTime candidate)
        {
            return !current.HasValue || candidate > current.Value ? candidate : current;
        }
    }
}
