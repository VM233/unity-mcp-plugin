using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Ticket-based request queue for multi-agent parallel access to Unity Editor.
    /// Implements fair round-robin scheduling across agents, read batching,
    /// and supports both async (ticket polling) and sync (blocking) modes.
    ///
    /// Architecture:
    ///   HTTP request → SubmitRequest (returns ticket immediately)
    ///                → ProcessNextRequests (called from EditorApplication.update on main thread)
    ///                → Agent polls GetTicketStatus for result
    ///
    /// Thread safety:
    ///   - _queueLock protects all queue/ticket/session state
    ///   - Actions execute OUTSIDE the lock on the main thread (prevents deadlocks)
    ///   - Synchronous callers use ManualResetEventSlim per ticket
    /// </summary>
    public static class MCPRequestQueue
    {
        // ═══════════════════════════════════════════════════════
        //  Ticket
        // ═══════════════════════════════════════════════════════

        public enum RequestStatus { Queued, Executing, Completed, Failed, TimedOut, LostAfterReload }

        public class RequestTicket
        {
            public long   TicketId    { get; set; }
            public string AgentId     { get; set; }
            public string ActionName  { get; set; }
            public RequestStatus Status { get; set; }

            // The actual work to execute on the main thread (sync)
            internal Func<object> Action { get; set; }

            // Deferred work whose result arrives via callback (async Unity APIs)
            internal Action<Action<object>> DeferredAction { get; set; }

            // Result / error
            public object Result       { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorCode    { get; set; }
            public bool   Retryable    { get; set; }

            // Timing
            public DateTime  SubmittedAt   { get; set; }
            public DateTime? CompletedAt   { get; set; }
            public int       QueuePosition { get; set; }

            public long ExecutionTimeMs =>
                CompletedAt.HasValue
                    ? (long)(CompletedAt.Value - SubmittedAt).TotalMilliseconds
                    : -1;
        }

        // ═══════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════

        // Ticket ID generator (thread-safe via Interlocked)
        private static long _nextTicketId;

        // Per-agent FIFO queues for fair round-robin
        private static readonly Dictionary<string, Queue<RequestTicket>> _agentQueues
            = new Dictionary<string, Queue<RequestTicket>>();

        // Stable round-robin order + index
        private static readonly List<string> _rrOrder = new List<string>();
        private static int _rrIndex;

        // Completed/failed tickets cached for polling
        private static readonly Dictionary<long, RequestTicket> _completedTickets
            = new Dictionary<long, RequestTicket>();

        // In-flight tickets (dequeued, currently executing on main thread)
        // Prevents 404 race condition when polling during slow executions (e.g. execute_code)
        private static readonly Dictionary<long, RequestTicket> _executingTickets
            = new Dictionary<long, RequestTicket>();

        // Reload snapshots are small status records persisted through domain reload.
        private static readonly Dictionary<long, Dictionary<string, object>> _reloadedTicketSnapshots
            = new Dictionary<long, Dictionary<string, object>>();
        private static bool _persistentSnapshotsLoaded;

        // Synchronous waiters (backward compat)
        private static readonly Dictionary<long, ManualResetEventSlim> _waiters
            = new Dictionary<long, ManualResetEventSlim>();

        // Session tracking
        private static readonly Dictionary<string, MCPAgentSession> _sessions
            = new Dictionary<string, MCPAgentSession>();

        // Single lock for all mutable state
        private static readonly object _queueLock = new object();

        // Cleanup cadence
        private static int _frameTick;
        private const int CleanupEveryNFrames        = 100;
        private const int CompletedCacheLifetimeSec   = 600;
        private const int TimedOutCacheLifetimeSec    = 300;
        private const int StaleExecutingLifetimeSec   = 120;
        private const string PersistentTicketSnapshotKey = "UnityMCP_RequestQueue_TicketSnapshots_v2";
        public const int SyncTimeoutMs                = 30_000;
        private const int MaxReadBatchSize            = 5;

        // ═══════════════════════════════════════════════════════
        //  Public API — Submit
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Submit a request to the queue. Returns a ticket immediately (non-blocking).
        /// The action will be executed on the main thread when its turn comes.
        /// </summary>
        public static RequestTicket SubmitRequest(string agentId, string actionName, Func<object> action)
        {
            EnsurePersistentSnapshotsLoaded();
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";

            var ticket = new RequestTicket
            {
                TicketId    = Interlocked.Increment(ref _nextTicketId),
                AgentId     = agentId,
                ActionName  = actionName,
                Status      = RequestStatus.Queued,
                SubmittedAt = DateTime.UtcNow,
                Action      = action,
            };

            lock (_queueLock)
            {
                // Ensure agent queue exists
                if (!_agentQueues.ContainsKey(agentId))
                {
                    _agentQueues[agentId] = new Queue<RequestTicket>();
                    _rrOrder.Add(agentId);
                }

                ticket.QueuePosition = _agentQueues[agentId].Count;
                _agentQueues[agentId].Enqueue(ticket);

                // Session bookkeeping
                EnsureSession(agentId).LogAction(actionName);
                _sessions[agentId].IncrementQueuedRequest();
                PersistTicketSnapshotsLocked();
            }

            return ticket;
        }

        /// <summary>
        /// Submit a deferred request whose result arrives via callback.
        /// Use for Unity APIs with async callbacks (e.g. TestRunnerApi.RetrieveTestList).
        /// </summary>
        public static RequestTicket SubmitDeferredRequest(string agentId, string actionName,
            Action<Action<object>> deferredAction)
        {
            EnsurePersistentSnapshotsLoaded();
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";

            var ticket = new RequestTicket
            {
                TicketId       = Interlocked.Increment(ref _nextTicketId),
                AgentId        = agentId,
                ActionName     = actionName,
                Status         = RequestStatus.Queued,
                SubmittedAt    = DateTime.UtcNow,
                DeferredAction = deferredAction,
            };

            lock (_queueLock)
            {
                if (!_agentQueues.ContainsKey(agentId))
                {
                    _agentQueues[agentId] = new Queue<RequestTicket>();
                    _rrOrder.Add(agentId);
                }

                ticket.QueuePosition = _agentQueues[agentId].Count;
                _agentQueues[agentId].Enqueue(ticket);

                EnsureSession(agentId).LogAction(actionName);
                _sessions[agentId].IncrementQueuedRequest();
                PersistTicketSnapshotsLocked();
            }

            return ticket;
        }

        /// <summary>
        /// Backward-compatible synchronous mode: submit → wait → return result.
        /// Used by the existing HandleRequest path (direct HTTP calls).
        /// </summary>
        public static object ExecuteWithTracking(string agentId, string actionName, Func<object> action)
        {
            var ticket = SubmitRequest(agentId, actionName, action);
            return WaitForTicket(ticket);
        }

        public static object ExecuteDeferredWithTracking(string agentId, string actionName,
            Action<Action<object>> deferredAction)
        {
            var ticket = SubmitDeferredRequest(agentId, actionName, deferredAction);
            return WaitForTicket(ticket);
        }

        private static object WaitForTicket(RequestTicket ticket)
        {
            var waiter = new ManualResetEventSlim(false);
            lock (_queueLock) { _waiters[ticket.TicketId] = waiter; }

            try
            {
                if (!waiter.Wait(SyncTimeoutMs))
                {
                    lock (_queueLock)
                    {
                        ticket.ErrorMessage = $"Timed out after {SyncTimeoutMs / 1000}s waiting for main thread";
                        ticket.ErrorCode = "sync_wait_timeout";
                        ticket.Retryable = true;
                        PersistTicketSnapshotsLocked();
                    }
                    return MCPResponse.Error(ticket.ErrorMessage, "sync_wait_timeout", true,
                        new Dictionary<string, object>
                        {
                            { "ticketId", ticket.TicketId },
                            { "status", ticket.Status.ToString() },
                            { "pollRoute", "queue/status" },
                        });
                }

                lock (_queueLock)
                {
                    if (_completedTickets.TryGetValue(ticket.TicketId, out var done))
                    {
                        if (done.Status == RequestStatus.Failed || done.Status == RequestStatus.TimedOut)
                        {
                            var error = MCPResponse.NormalizeError(done.Result, done.ErrorCode ?? "request_failed",
                                done.Retryable);
                            error["ticketId"] = done.TicketId;
                            return error;
                        }

                        return done.Result;
                    }
                }

                return MCPResponse.Error("Ticket completed but its result was not available.",
                    "ticket_result_missing", true, new Dictionary<string, object>
                {
                    { "ticketId", ticket.TicketId },
                    { "pollRoute", "queue/status" },
                });
            }
            finally
            {
                waiter.Dispose();
                lock (_queueLock) { _waiters.Remove(ticket.TicketId); }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Main-Thread Processing (called from EditorApplication.update)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Dequeue and execute requests on the main thread.
        /// Processes 1 write OR up to 5 reads per call (batching reads).
        /// Also runs periodic cleanup.
        /// </summary>
        public static void ProcessNextRequests()
        {
            // --- Cleanup cadence ---
            if (++_frameTick >= CleanupEveryNFrames)
            {
                _frameTick = 0;
                RunCleanup();
            }

            // --- Dequeue ---
            List<RequestTicket> batch;
            lock (_queueLock)
            {
                batch = DequeueNextBatch();
                if (batch == null || batch.Count == 0) return;

                // Mark all as executing and track in-flight
                foreach (var t in batch)
                {
                    t.Status = RequestStatus.Executing;
                    _executingTickets[t.TicketId] = t;
                }
            }

            // --- Execute OUTSIDE lock (main thread) ---
            foreach (var ticket in batch)
            {
                // Capture undo group before execution for undo support
                int undoGroupBefore = UnityEditor.Undo.GetCurrentGroup();

                // Deferred actions complete via callback on a future editor frame.
                if (ticket.DeferredAction != null)
                {
                    var deferredTicket = ticket; // capture for closure
                    try
                    {
                        deferredTicket.DeferredAction(result =>
                        {
                            CompleteTicketFromResult(deferredTicket, result);

                            lock (_queueLock)
                            {
                                _executingTickets.Remove(deferredTicket.TicketId);
                                _completedTickets[deferredTicket.TicketId] = deferredTicket;
                                if (_waiters.TryGetValue(deferredTicket.TicketId, out var w))
                                    w.Set();
                                if (_sessions.TryGetValue(deferredTicket.AgentId, out var s))
                                    s.IncrementCompletedRequest(deferredTicket.ExecutionTimeMs);
                                PersistTicketSnapshotsLocked();
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        FailTicket(deferredTicket, ex.Message, "exception", false,
                            new Dictionary<string, object> { { "stackTrace", ex.StackTrace } });
                        Debug.LogError($"[Unity MCP Queue] Deferred ticket {ticket.TicketId} ({ticket.ActionName}) failed: {ex.Message}");

                        lock (_queueLock)
                        {
                            _executingTickets.Remove(deferredTicket.TicketId);
                            _completedTickets[deferredTicket.TicketId] = deferredTicket;
                            if (_waiters.TryGetValue(deferredTicket.TicketId, out var w))
                                w.Set();
                            PersistTicketSnapshotsLocked();
                        }
                    }
                    continue; // Skip normal completion — callback handles it
                }

                try
                {
                    CompleteTicketFromResult(ticket, ticket.Action());
                }
                catch (Exception ex)
                {
                    FailTicket(ticket, ex.Message, "exception", false,
                        new Dictionary<string, object> { { "stackTrace", ex.StackTrace } });
                    Debug.LogError($"[Unity MCP Queue] Ticket {ticket.TicketId} ({ticket.ActionName}) failed: {ex.Message}");
                }
                ticket.Action      = null; // Free the closure

                int undoGroupAfter = UnityEditor.Undo.GetCurrentGroup();

                // Record action in history
                try
                {
                    var record = new MCPActionRecord
                    {
                        Timestamp       = ticket.CompletedAt ?? DateTime.UtcNow,
                        AgentId         = ticket.AgentId,
                        ActionName      = ticket.ActionName,
                        Category        = MCPActionRecord.ExtractCategory(ticket.ActionName),
                        Status          = ticket.Status.ToString(),
                        ExecutionTimeMs = ticket.ExecutionTimeMs,
                        ErrorMessage    = ticket.ErrorMessage,
                        UndoGroup       = undoGroupAfter != undoGroupBefore ? undoGroupBefore : -1,
                    };

                    // Try to extract target object info from result
                    if (ticket.Status == RequestStatus.Completed)
                        record.ExtractTargetFromResult(ticket.Result);

                    MCPActionHistory.RecordAction(record);

                    // Also log to the agent session's structured log
                    lock (_queueLock)
                    {
                        if (_sessions.TryGetValue(ticket.AgentId, out var agentSession))
                            agentSession.LogStructuredAction(record);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Unity MCP Queue] Failed to record action history: {ex.Message}");
                }

                // Move to completed cache, remove from in-flight, and signal waiters
                lock (_queueLock)
                {
                    _executingTickets.Remove(ticket.TicketId);
                    _completedTickets[ticket.TicketId] = ticket;

                    if (_waiters.TryGetValue(ticket.TicketId, out var waiter))
                        waiter.Set();

                    if (_sessions.TryGetValue(ticket.AgentId, out var session))
                        session.IncrementCompletedRequest(ticket.ExecutionTimeMs);
                    PersistTicketSnapshotsLocked();
                }
            }
        }

        private static void CompleteTicketFromResult(RequestTicket ticket, object result)
        {
            if (MCPResponse.TryGetError(result, out var message, out var errorCode, out var retryable))
            {
                FailTicket(ticket, message, errorCode, retryable);
                ticket.Result = MCPResponse.NormalizeError(result, errorCode, retryable);
                return;
            }

            ticket.Result = result;
            ticket.Status = RequestStatus.Completed;
            ticket.ErrorMessage = null;
            ticket.ErrorCode = null;
            ticket.Retryable = false;
            ticket.CompletedAt = DateTime.UtcNow;
            ticket.Action = null;
            ticket.DeferredAction = null;
        }

        private static void FailTicket(RequestTicket ticket, string message, string errorCode, bool retryable,
            Dictionary<string, object> extra = null)
        {
            ticket.Status = RequestStatus.Failed;
            ticket.ErrorMessage = message ?? "Operation failed.";
            ticket.ErrorCode = string.IsNullOrEmpty(errorCode) ? "request_failed" : errorCode;
            ticket.Retryable = retryable;
            ticket.Result = MCPResponse.Error(ticket.ErrorMessage, ticket.ErrorCode, retryable, extra);
            ticket.CompletedAt = DateTime.UtcNow;
            ticket.Action = null;
            ticket.DeferredAction = null;
        }

        // ═══════════════════════════════════════════════════════
        //  Query API
        // ═══════════════════════════════════════════════════════

        /// <summary>Returns ticket info for polling, or null if not found/expired.</summary>
        public static Dictionary<string, object> GetTicketStatus(long ticketId)
        {
            EnsurePersistentSnapshotsLoaded();
            lock (_queueLock)
            {
                // Check completed cache first
                if (_completedTickets.TryGetValue(ticketId, out var done))
                    return TicketToDict(done);

                // Check in-flight (currently executing on main thread)
                if (_executingTickets.TryGetValue(ticketId, out var executing))
                    return TicketToDict(executing);

                // Check active queues
                foreach (var q in _agentQueues.Values)
                    foreach (var t in q)
                        if (t.TicketId == ticketId)
                            return TicketToDict(t);

                if (_reloadedTicketSnapshots.TryGetValue(ticketId, out var snapshot))
                    return new Dictionary<string, object>(snapshot);
            }
            return null;
        }

        /// <summary>Returns overall queue stats.</summary>
        public static Dictionary<string, object> GetQueueInfo()
        {
            lock (_queueLock)
            {
                int totalQueued = 0;
                var perAgent = new Dictionary<string, object>();

                foreach (var kvp in _agentQueues)
                {
                    int c = kvp.Value.Count;
                    perAgent[kvp.Key] = c;
                    totalQueued += c;
                }

                return new Dictionary<string, object>
                {
                    { "totalQueued",          totalQueued },
                    { "activeAgents",         _agentQueues.Count },
                    { "executingCount",       _executingTickets.Count },
                    { "completedCacheSize",   _completedTickets.Count },
                    { "perAgentQueued",        perAgent },
                    { "totalSessionsTracked", _sessions.Count },
                };
            }
        }

        public static List<Dictionary<string, object>> GetActiveSessions()
        {
            var list = new List<Dictionary<string, object>>();
            lock (_queueLock)
            {
                foreach (var s in _sessions.Values)
                    if (s.IsActive) list.Add(s.ToDict());
            }
            return list;
        }

        public static List<string> GetAgentLog(string agentId)
        {
            lock (_queueLock)
            {
                if (_sessions.TryGetValue(agentId, out var s))
                    return s.GetLog();
            }
            return new List<string>();
        }

        public static int TotalSessionCount
        {
            get { lock (_queueLock) return _sessions.Count; }
        }

        public static int ActiveSessionCount
        {
            get
            {
                int n = 0;
                lock (_queueLock)
                    foreach (var s in _sessions.Values)
                        if (s.IsActive) n++;
                return n;
            }
        }

        public static int TotalQueuedCount
        {
            get
            {
                int n = 0;
                lock (_queueLock)
                    foreach (var q in _agentQueues.Values) n += q.Count;
                return n;
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Internals
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Fair round-robin dequeue. Returns 1 write OR up to MaxReadBatchSize reads.
        /// Must be called under _queueLock.
        /// </summary>
        private static List<RequestTicket> DequeueNextBatch()
        {
            if (_rrOrder.Count == 0) return null;

            // Advance round-robin to find an agent with work
            int startIndex = _rrIndex;
            RequestTicket first = null;
            string firstAgent = null;

            for (int i = 0; i < _rrOrder.Count; i++)
            {
                int idx = (_rrIndex + i) % _rrOrder.Count;
                string agent = _rrOrder[idx];

                if (_agentQueues.TryGetValue(agent, out var q) && q.Count > 0)
                {
                    first = q.Peek();
                    firstAgent = agent;
                    _rrIndex = (idx + 1) % _rrOrder.Count;
                    break;
                }
            }

            if (first == null)
            {
                PurgeEmptyQueues();
                return null;
            }

            var batch = new List<RequestTicket>();

            if (!IsReadOperation(first.ActionName))
            {
                // Single write request
                _agentQueues[firstAgent].Dequeue();
                batch.Add(first);
            }
            else
            {
                // Batch reads across agents (round-robin)
                int collected = 0;
                int scanIdx = (_rrIndex - 1 + _rrOrder.Count) % _rrOrder.Count;

                for (int pass = 0; collected < MaxReadBatchSize && pass < _rrOrder.Count * MaxReadBatchSize; pass++)
                {
                    int idx = (scanIdx + pass) % _rrOrder.Count;
                    string agent = _rrOrder[idx];

                    if (!_agentQueues.TryGetValue(agent, out var q) || q.Count == 0)
                        continue;

                    var peek = q.Peek();
                    if (!IsReadOperation(peek.ActionName))
                        continue;

                    batch.Add(q.Dequeue());
                    collected++;
                }
            }

            PurgeEmptyQueues();
            return batch;
        }

        private static bool IsReadOperation(string actionName)
        {
            if (string.IsNullOrEmpty(actionName)) return false;

            // Match API path patterns that are read-only
            string lower = actionName.ToLower();
            return lower == "ping"
                || lower.EndsWith("/info")
                || lower.EndsWith("/list")
                || lower.EndsWith("/log")
                || lower.EndsWith("/stats")
                || lower.EndsWith("/get")
                || lower.StartsWith("search/")
                || lower.StartsWith("agents/")
                || lower.StartsWith("queue/")
                || lower == "scene/info"
                || lower == "scene/hierarchy"
                || lower == "editor/state"
                || lower == "project/info"
                || lower == "console/log"
                || lower == "debug/attach-unity"
                || lower == "debug/stack-trace"
                || lower == "debug/variables"
                || lower.StartsWith("profiler/")
                || lower.StartsWith("debugger/")
                || lower.StartsWith("selection/get")
                || lower.StartsWith("selection/find")
                || lower.Contains("/info")
                || lower.Contains("/list")
                || lower.Contains("/get-")
                || lower.Contains("/status");
        }

        private static void PurgeEmptyQueues()
        {
            for (int i = _rrOrder.Count - 1; i >= 0; i--)
            {
                string agent = _rrOrder[i];
                if (!_agentQueues.ContainsKey(agent) || _agentQueues[agent].Count == 0)
                {
                    _agentQueues.Remove(agent);
                    _rrOrder.RemoveAt(i);
                    if (_rrIndex > i) _rrIndex = Math.Max(0, _rrIndex - 1);
                }
            }
            if (_rrOrder.Count > 0 && _rrIndex >= _rrOrder.Count)
                _rrIndex = 0;
        }

        private static MCPAgentSession EnsureSession(string agentId)
        {
            if (!_sessions.TryGetValue(agentId, out var session))
            {
                session = new MCPAgentSession
                {
                    AgentId     = agentId,
                    ConnectedAt = DateTime.UtcNow,
                };
                _sessions[agentId] = session;
            }
            return session;
        }

        private static void RunCleanup()
        {
            lock (_queueLock)
            {
                var now  = DateTime.UtcNow;
                var kill = new List<long>();

                foreach (var kvp in _completedTickets)
                {
                    var t = kvp.Value;
                    if (!t.CompletedAt.HasValue) continue;

                    double age = (now - t.CompletedAt.Value).TotalSeconds;
                    if (t.Status == RequestStatus.TimedOut && age > TimedOutCacheLifetimeSec)
                        kill.Add(t.TicketId);
                    else if (age > CompletedCacheLifetimeSec)
                        kill.Add(t.TicketId);
                }

                foreach (var id in kill)
                    _completedTickets.Remove(id);

                // Safety valve: fail stale executing tickets instead of making polling 404.
                var staleExecuting = new List<RequestTicket>();
                foreach (var kvp in _executingTickets)
                {
                    double age = (now - kvp.Value.SubmittedAt).TotalSeconds;
                    if (age > StaleExecutingLifetimeSec)
                        staleExecuting.Add(kvp.Value);
                }
                foreach (var ticket in staleExecuting)
                {
                    FailTicket(ticket,
                        $"Request exceeded the stale execution limit ({StaleExecutingLifetimeSec}s). Resubmit if the Unity operation did not complete.",
                        "stale_execution", true);
                    _executingTickets.Remove(ticket.TicketId);
                    _completedTickets[ticket.TicketId] = ticket;
                    if (_waiters.TryGetValue(ticket.TicketId, out var waiter))
                        waiter.Set();
                }

                PersistTicketSnapshotsLocked();
            }
        }

        private static void EnsurePersistentSnapshotsLoaded()
        {
            lock (_queueLock)
            {
                if (_persistentSnapshotsLoaded)
                    return;

                _persistentSnapshotsLoaded = true;
                string json = SessionState.GetString(PersistentTicketSnapshotKey, "");
                if (string.IsNullOrEmpty(json))
                    return;

                var snapshots = MiniJson.Deserialize(json) as List<object>;
                if (snapshots == null)
                    return;

                long maxTicketId = 0;
                foreach (var item in snapshots)
                {
                    var snapshot = MCPResponse.ToDictionary(item);
                    if (snapshot == null)
                        continue;

                    if (!TryGetLong(snapshot, "ticketId", out var ticketId))
                        continue;

                    maxTicketId = Math.Max(maxTicketId, ticketId);
                    _reloadedTicketSnapshots[ticketId] = BuildReloadedSnapshot(snapshot);
                }

                if (maxTicketId > _nextTicketId)
                    Interlocked.Exchange(ref _nextTicketId, maxTicketId);
            }
        }

        private static Dictionary<string, object> BuildReloadedSnapshot(Dictionary<string, object> snapshot)
        {
            var restored = new Dictionary<string, object>(snapshot);
            string previousStatus = restored.TryGetValue("status", out var status) ? status?.ToString() : "";
            restored["previousStatus"] = previousStatus;
            restored["status"] = RequestStatus.LostAfterReload.ToString();
            restored["success"] = false;
            restored["error"] = "Ticket state was lost after a Unity domain reload. Resubmit the request.";
            restored["message"] = restored["error"];
            restored["errorCode"] = "ticket_lost_after_reload";
            restored["retryable"] = true;
            restored["result"] = MCPResponse.Error(restored["error"].ToString(), "ticket_lost_after_reload", true);
            return restored;
        }

        private static void PersistTicketSnapshotsLocked()
        {
            var snapshots = new List<object>();

            foreach (var agentQueue in _agentQueues.Values)
            {
                foreach (var ticket in agentQueue)
                    snapshots.Add(SnapshotTicket(ticket));
            }

            foreach (var ticket in _executingTickets.Values)
                snapshots.Add(SnapshotTicket(ticket));

            foreach (var ticket in _completedTickets.Values.OrderByDescending(t => t.SubmittedAt).Take(100))
                snapshots.Add(SnapshotTicket(ticket));

            SessionState.SetString(PersistentTicketSnapshotKey, MiniJson.Serialize(snapshots));
        }

        private static Dictionary<string, object> SnapshotTicket(RequestTicket ticket)
        {
            var snapshot = new Dictionary<string, object>
            {
                { "ticketId", ticket.TicketId },
                { "agentId", ticket.AgentId },
                { "actionName", ticket.ActionName },
                { "status", ticket.Status.ToString() },
                { "queuePosition", ticket.QueuePosition },
                { "submittedAt", ticket.SubmittedAt.ToString("O") },
                { "executionTimeMs", ticket.ExecutionTimeMs },
                { "errorMessage", ticket.ErrorMessage ?? "" },
                { "errorCode", ticket.ErrorCode ?? "" },
                { "retryable", ticket.Retryable },
            };

            if (ticket.CompletedAt.HasValue)
                snapshot["completedAt"] = ticket.CompletedAt.Value.ToString("O");

            return snapshot;
        }

        private static bool TryGetLong(Dictionary<string, object> dictionary, string key, out long value)
        {
            value = 0;
            return dictionary.TryGetValue(key, out var raw) &&
                   raw != null &&
                   long.TryParse(raw.ToString(), out value);
        }

        private static Dictionary<string, object> TicketToDict(RequestTicket t)
        {
            var dict = new Dictionary<string, object>
            {
                { "ticketId",        t.TicketId },
                { "agentId",         t.AgentId },
                { "actionName",      t.ActionName },
                { "status",          t.Status.ToString() },
                { "queuePosition",   t.QueuePosition },
                { "submittedAt",     t.SubmittedAt.ToString("O") },
                { "executionTimeMs", t.ExecutionTimeMs },
                { "errorMessage",    t.ErrorMessage ?? "" },
                { "errorCode",       t.ErrorCode ?? "" },
                { "retryable",       t.Retryable },
            };

            if (t.CompletedAt.HasValue)
                dict["completedAt"] = t.CompletedAt.Value.ToString("O");

            // Include result for completed tickets
            if (t.Status == RequestStatus.Completed || t.Status == RequestStatus.Failed)
                dict["result"] = t.Result;

            return dict;
        }
    }
}
