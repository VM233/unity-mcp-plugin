using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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

        public enum RequestStatus
        {
            Queued, Executing, Completed, Failed, TimedOut, Canceled, UncertainAfterReload
        }

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
            internal Action<Action<object>, Action<object>> ProgressiveDeferredAction { get; set; }

            // Result / error
            public object Result       { get; set; }
            public object Progress     { get; set; }
            public string ErrorMessage { get; set; }
            public string ErrorCode    { get; set; }
            public bool   Retryable    { get; set; }
            public string RequestKey   { get; set; }
            public Dictionary<string, object> PersistentArguments { get; set; }
            public string PersistentBody { get; set; }
            public string PersistentMethod { get; set; }
            public bool IsReadOnly { get; set; }
            public int ResumeCount { get; set; }

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
        private static readonly Dictionary<long, List<ManualResetEventSlim>> _waiters
            = new Dictionary<long, List<ManualResetEventSlim>>();

        // Session tracking
        private static readonly Dictionary<string, MCPAgentSession> _sessions
            = new Dictionary<string, MCPAgentSession>();

        // Single lock for all mutable state
        private static readonly object _queueLock = new object();

        // Cleanup cadence. Ticket lifetimes are time-based, so cleanup must not scale with
        // Editor frame rate or perform periodic disk I/O during gameplay.
        private static double _nextCleanupAt;
        private const double CleanupIntervalSeconds   = 30.0;
        private const int CompletedCacheLifetimeSec   = 600;
        private const int TimedOutCacheLifetimeSec    = 300;
        private const int StaleExecutingLifetimeSec   = 120;
        private const string PersistentTicketSnapshotFileName = "request-queue-tickets-v2.json";
        public const int SyncTimeoutMs                = 30_000;
        private const int MaxReadBatchSize            = 5;
        private const int MaxTotalQueuedRequests      = 256;
        private const int MaxQueuedRequestsPerAgent   = 64;

        // ═══════════════════════════════════════════════════════
        //  Public API — Submit
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Submit a request to the queue. Returns a ticket immediately (non-blocking).
        /// The action will be executed on the main thread when its turn comes.
        /// </summary>
        public static RequestTicket SubmitRequest(string agentId, string actionName, Func<object> action)
        {
            return SubmitRequest(agentId, actionName, action, null, null, null,
                MCPToolMetadata.IsRouteReadOnly(actionName), out _);
        }

        public static RequestTicket SubmitPersistentRequest(string agentId, string actionName, string method,
            string body, string requestKey, out bool reused)
        {
            return SubmitRequest(agentId, actionName,
                () => MCPBridgeServer.ExecutePersistedRoute(actionName, method, body), requestKey, body, method,
                MCPToolMetadata.IsRouteReadOnly(actionName), out reused);
        }

        private static RequestTicket SubmitRequest(string agentId, string actionName, Func<object> action,
            string requestKey, string persistentBody, string persistentMethod, bool readOnly, out bool reused)
        {
            EnsurePersistentSnapshotsLoaded();
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";
            reused = false;

            lock (_queueLock)
            {
                if (!string.IsNullOrEmpty(requestKey))
                {
                    var existing = FindActiveTicketByRequestKeyLocked(requestKey);
                    if (existing != null)
                    {
                        reused = true;
                        return existing;
                    }
                    foreach (var completed in _completedTickets.Values)
                    {
                        if (completed.RequestKey == requestKey)
                        {
                            reused = true;
                            return completed;
                        }
                    }
                }

                int totalQueued = _agentQueues.Values.Sum(queue => queue.Count);
                int agentQueued = _agentQueues.TryGetValue(agentId, out var agentQueue) ? agentQueue.Count : 0;
                if (totalQueued >= MaxTotalQueuedRequests || agentQueued >= MaxQueuedRequestsPerAgent)
                    return CreateRejectedTicketLocked(agentId, actionName, requestKey, totalQueued, agentQueued);
            }

            var ticket = new RequestTicket
            {
                TicketId    = Interlocked.Increment(ref _nextTicketId),
                AgentId     = agentId,
                ActionName  = actionName,
                Status      = RequestStatus.Queued,
                SubmittedAt = DateTime.UtcNow,
                Action      = action,
                RequestKey = requestKey,
                PersistentBody = persistentBody,
                PersistentMethod = persistentMethod,
                IsReadOnly = readOnly,
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
            return SubmitDeferredRequest(agentId, actionName, (resolve, _) => deferredAction(resolve));
        }

        public static RequestTicket SubmitDeferredRequest(string agentId, string actionName,
            Action<Action<object>, Action<object>> deferredAction)
        {
            return SubmitDeferredRequest(agentId, actionName, deferredAction, null, null, false, out _);
        }

        public static RequestTicket SubmitDeferredRequest(string agentId, string actionName,
            Action<Action<object>, Action<object>> deferredAction, string requestKey, out bool reused)
        {
            return SubmitDeferredRequest(agentId, actionName, deferredAction, null, requestKey, true, out reused);
        }

        public static RequestTicket SubmitPersistentDeferredRequest(string agentId, string actionName,
            Action<Action<object>, Action<object>> deferredAction, string body, string requestKey, out bool reused)
        {
            var ticket = SubmitDeferredRequest(agentId, actionName, deferredAction, null, requestKey, true,
                out reused);
            lock (_queueLock)
            {
                ticket.PersistentBody = body ?? "";
                ticket.PersistentMethod = "POST";
                ticket.IsReadOnly = MCPToolMetadata.IsRouteReadOnly(actionName);
                PersistTicketSnapshotsLocked();
            }
            return ticket;
        }

        public static RequestTicket SubmitResumableEditorIdleWait(string agentId,
            Dictionary<string, object> args, out bool reused)
        {
            var normalized = MCPEditorCommands.NormalizeWaitForIdleArguments(args);
            var persistentArguments = new Dictionary<string, object>
            {
                { "timeoutMs", normalized["timeoutMs"] },
                { "stableFrames", normalized["stableFrames"] },
                { "stableMs", normalized["stableMs"] },
                { "_resumeCount", normalized["resumeCount"] },
                { "_deadlineUtc", DateTime.UtcNow.AddMilliseconds((int)normalized["timeoutMs"]).ToString("O") },
            };
            string requestKey = MCPEditorCommands.BuildWaitForIdleRequestKey(persistentArguments);
            return SubmitDeferredRequest(agentId, "wait/editor-idle",
                (resolve, _) => MCPEditorCommands.WaitForIdle(persistentArguments, resolve),
                persistentArguments, requestKey, false, out reused);
        }

        private static RequestTicket SubmitDeferredRequest(string agentId, string actionName,
            Action<Action<object>, Action<object>> deferredAction,
            Dictionary<string, object> persistentArguments, string requestKey, bool reuseCompleted,
            out bool reused)
        {
            EnsurePersistentSnapshotsLoaded();
            if (string.IsNullOrEmpty(agentId)) agentId = "anonymous";
            reused = false;

            lock (_queueLock)
            {
                if (!string.IsNullOrEmpty(requestKey))
                {
                    var existing = FindActiveTicketByRequestKeyLocked(requestKey);
                    if (existing != null)
                    {
                        reused = true;
                        return existing;
                    }
                    foreach (var completed in reuseCompleted
                                 ? _completedTickets.Values
                                 : Enumerable.Empty<RequestTicket>())
                    {
                        if (completed.RequestKey == requestKey)
                        {
                            reused = true;
                            return completed;
                        }
                    }
                }

                int totalQueued = _agentQueues.Values.Sum(queue => queue.Count);
                int agentQueued = _agentQueues.TryGetValue(agentId, out var currentQueue)
                    ? currentQueue.Count
                    : 0;
                if (totalQueued >= MaxTotalQueuedRequests || agentQueued >= MaxQueuedRequestsPerAgent)
                    return CreateRejectedTicketLocked(agentId, actionName, requestKey, totalQueued, agentQueued);

                var ticket = new RequestTicket
                {
                    TicketId       = Interlocked.Increment(ref _nextTicketId),
                    AgentId        = agentId,
                    ActionName     = actionName,
                    Status         = RequestStatus.Queued,
                    SubmittedAt    = DateTime.UtcNow,
                    ProgressiveDeferredAction = deferredAction,
                    PersistentArguments = persistentArguments,
                    RequestKey = requestKey,
                    ResumeCount = GetInt(persistentArguments, "_resumeCount", 0),
                    IsReadOnly = MCPToolMetadata.IsRouteReadOnly(actionName),
                };

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
                return ticket;
            }
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

        public static object ExecutePersistentWithTracking(string agentId, string actionName, string method,
            string body, string requestKey)
        {
            var ticket = SubmitPersistentRequest(agentId, actionName, method, body, requestKey, out _);
            return WaitForTicket(ticket);
        }

        public static object ExecuteDeferredWithTracking(string agentId, string actionName,
            Action<Action<object>> deferredAction)
        {
            return ExecuteDeferredWithTracking(agentId, actionName, (resolve, _) => deferredAction(resolve));
        }

        public static object ExecuteDeferredWithTracking(string agentId, string actionName,
            Action<Action<object>, Action<object>> deferredAction)
        {
            var ticket = SubmitDeferredRequest(agentId, actionName, deferredAction);
            return WaitForTicket(ticket);
        }

        public static object ExecuteResumableEditorIdleWait(string agentId, Dictionary<string, object> args)
        {
            var ticket = SubmitResumableEditorIdleWait(agentId, args, out _);
            return WaitForTicket(ticket);
        }

        private static object WaitForTicket(RequestTicket ticket)
        {
            var waiter = new ManualResetEventSlim(false);
            lock (_queueLock)
            {
                if (_completedTickets.ContainsKey(ticket.TicketId))
                {
                    waiter.Set();
                }
                else
                {
                    if (!_waiters.TryGetValue(ticket.TicketId, out var waiters))
                    {
                        waiters = new List<ManualResetEventSlim>();
                        _waiters[ticket.TicketId] = waiters;
                    }
                    waiters.Add(waiter);
                }
            }

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
                        if (done.Status == RequestStatus.Failed || done.Status == RequestStatus.TimedOut ||
                            done.Status == RequestStatus.Canceled ||
                            done.Status == RequestStatus.UncertainAfterReload)
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
                lock (_queueLock)
                {
                    if (_waiters.TryGetValue(ticket.TicketId, out var waiters))
                    {
                        waiters.Remove(waiter);
                        if (waiters.Count == 0)
                            _waiters.Remove(ticket.TicketId);
                    }
                }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Main-Thread Processing (called from EditorApplication.update)
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Dequeue and execute requests on the main thread.
        /// Processes 1 write OR up to <paramref name="maxRequests"/> reads per call.
        /// The bridge passes 1 so a post-reload backlog is spread across Editor updates.
        /// Also runs periodic cleanup.
        /// </summary>
        public static int ProcessNextRequests(int maxRequests = MaxReadBatchSize)
        {
            if (maxRequests <= 0)
                return 0;

            EnsurePersistentSnapshotsLoaded();

            // --- Cleanup cadence ---
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now >= _nextCleanupAt)
            {
                _nextCleanupAt = now + CleanupIntervalSeconds;
                RunCleanup();
            }

            // --- Dequeue ---
            List<RequestTicket> batch;
            lock (_queueLock)
            {
                if (HasExecutingWriteLocked())
                    return 0;

                batch = DequeueNextBatch(allowWrites: _executingTickets.Count == 0,
                    maxRequests: maxRequests);
                if (batch == null || batch.Count == 0) return 0;

                // Mark all as executing and track in-flight
                foreach (var t in batch)
                {
                    t.Status = RequestStatus.Executing;
                    _executingTickets[t.TicketId] = t;
                }
                // Persist Executing before invoking Unity APIs. A domain reload can begin
                // inside the action, so persisting only after it returns can replay a
                // mutation that had already started.
                PersistTicketSnapshotsLocked();
            }

            // --- Execute OUTSIDE lock (main thread) ---
            foreach (var ticket in batch)
            {
                // Capture undo group before execution for undo support
                int undoGroupBefore = UnityEditor.Undo.GetCurrentGroup();

                // Deferred actions complete via callback on a future editor frame.
                if (ticket.DeferredAction != null || ticket.ProgressiveDeferredAction != null)
                {
                    var deferredTicket = ticket; // capture for closure
                    try
                    {
                        Action<object> resolve = result =>
                        {
                            CompleteTicketFromResult(deferredTicket, result);

                            lock (_queueLock)
                            {
                                _executingTickets.Remove(deferredTicket.TicketId);
                                _completedTickets[deferredTicket.TicketId] = deferredTicket;
                                SignalWaitersLocked(deferredTicket.TicketId);
                                if (_sessions.TryGetValue(deferredTicket.AgentId, out var s))
                                    s.IncrementCompletedRequest(deferredTicket.ExecutionTimeMs);
                                PersistTicketSnapshotsLocked();
                            }
                        };

                        Action<object> progress = progressValue =>
                        {
                            lock (_queueLock)
                            {
                                deferredTicket.Progress = progressValue;
                                PersistTicketSnapshotsLocked();
                            }
                        };

                        if (deferredTicket.ProgressiveDeferredAction != null)
                            deferredTicket.ProgressiveDeferredAction(resolve, progress);
                        else
                            deferredTicket.DeferredAction(resolve);
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
                            SignalWaitersLocked(deferredTicket.TicketId);
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

                    SignalWaitersLocked(ticket.TicketId);

                    if (_sessions.TryGetValue(ticket.AgentId, out var session))
                        session.IncrementCompletedRequest(ticket.ExecutionTimeMs);
                    PersistTicketSnapshotsLocked();
                }
            }

            return batch.Count;
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
            ticket.Progress = null;
            ticket.ErrorMessage = null;
            ticket.ErrorCode = null;
            ticket.Retryable = false;
            ticket.CompletedAt = DateTime.UtcNow;
            ticket.Action = null;
            ticket.DeferredAction = null;
            ticket.ProgressiveDeferredAction = null;
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
            ticket.ProgressiveDeferredAction = null;
        }

        // ═══════════════════════════════════════════════════════
        //  Query API
        // ═══════════════════════════════════════════════════════

        /// <summary>Returns ticket info for polling, or null if not found/expired.</summary>
        public static Dictionary<string, object> GetTicketStatus(long ticketId)
        {
            return GetTicketStatus(ticketId, null, false);
        }

        public static Dictionary<string, object> GetTicketStatus(long ticketId, string agentId,
            bool enforceOwnership)
        {
            EnsurePersistentSnapshotsLoaded();
            lock (_queueLock)
            {
                // Check completed cache first
                if (_completedTickets.TryGetValue(ticketId, out var done))
                    return OwnedTicketToDict(done, agentId, enforceOwnership);

                // Check in-flight (currently executing on main thread)
                if (_executingTickets.TryGetValue(ticketId, out var executing))
                    return OwnedTicketToDict(executing, agentId, enforceOwnership);

                // Check active queues
                foreach (var q in _agentQueues.Values)
                    foreach (var t in q)
                        if (t.TicketId == ticketId)
                            return OwnedTicketToDict(t, agentId, enforceOwnership);

                if (_reloadedTicketSnapshots.TryGetValue(ticketId, out var snapshot))
                {
                    if (enforceOwnership && snapshot.TryGetValue("agentId", out object owner) &&
                        !string.Equals(owner?.ToString(), agentId, StringComparison.Ordinal))
                        return MCPResponse.Error("Ticket belongs to another agent.", "ticket_owner_mismatch");
                    return new Dictionary<string, object>(snapshot);
                }
            }
            return null;
        }

        public static Dictionary<string, object> CancelTicket(long ticketId, string agentId)
        {
            EnsurePersistentSnapshotsLoaded();
            lock (_queueLock)
            {
                foreach (string owner in _agentQueues.Keys.ToList())
                {
                    var queue = _agentQueues[owner];
                    var found = queue.FirstOrDefault(ticket => ticket.TicketId == ticketId);
                    if (found == null) continue;
                    if (!string.Equals(found.AgentId, agentId, StringComparison.Ordinal))
                        return MCPResponse.Error("Ticket belongs to another agent.", "ticket_owner_mismatch");
                    var retained = new Queue<RequestTicket>();
                    RequestTicket canceled = null;
                    while (queue.Count > 0)
                    {
                        var ticket = queue.Dequeue();
                        if (ticket.TicketId == ticketId) canceled = ticket;
                        else retained.Enqueue(ticket);
                    }
                    _agentQueues[owner] = retained;
                    canceled.Status = RequestStatus.Canceled;
                    canceled.ErrorMessage = "Canceled before execution.";
                    canceled.ErrorCode = "request_canceled";
                    canceled.CompletedAt = DateTime.UtcNow;
                    canceled.Result = MCPResponse.Error(canceled.ErrorMessage, canceled.ErrorCode);
                    canceled.Action = null;
                    canceled.DeferredAction = null;
                    canceled.ProgressiveDeferredAction = null;
                    _completedTickets[ticketId] = canceled;
                    SignalWaitersLocked(ticketId);
                    PurgeEmptyQueues();
                    PersistTicketSnapshotsLocked();
                    return new Dictionary<string, object>
                    {
                        { "success", true }, { "ticketId", ticketId }, { "status", "Canceled" },
                        { "canceledBeforeExecution", true },
                    };
                }

                if (_executingTickets.TryGetValue(ticketId, out var executing))
                {
                    if (!string.Equals(executing.AgentId, agentId, StringComparison.Ordinal))
                        return MCPResponse.Error("Ticket belongs to another agent.", "ticket_owner_mismatch");
                    return MCPResponse.Error(
                        "The request is already executing and cannot be safely preempted. Poll it to completion.",
                        "request_not_cancelable");
                }

                return MCPResponse.Error($"Ticket {ticketId} was not found or is already terminal.",
                    "ticket_not_found");
            }
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

        private static RequestTicket FindActiveTicketByRequestKeyLocked(string requestKey)
        {
            foreach (var ticket in _executingTickets.Values)
            {
                if (ticket.RequestKey == requestKey)
                    return ticket;
            }

            foreach (var queue in _agentQueues.Values)
            {
                foreach (var ticket in queue)
                {
                    if (ticket.RequestKey == requestKey)
                        return ticket;
                }
            }

            return null;
        }

        private static RequestTicket CreateRejectedTicketLocked(string agentId, string actionName,
            string requestKey, int totalQueued, int agentQueued)
        {
            var ticket = new RequestTicket
            {
                TicketId = Interlocked.Increment(ref _nextTicketId), AgentId = agentId,
                ActionName = actionName, RequestKey = requestKey, Status = RequestStatus.Failed,
                SubmittedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow,
                ErrorMessage = totalQueued >= MaxTotalQueuedRequests
                    ? $"Queue capacity reached ({MaxTotalQueuedRequests})."
                    : $"Per-agent queue capacity reached ({MaxQueuedRequestsPerAgent}).",
                ErrorCode = "queue_capacity_reached", Retryable = true,
            };
            ticket.Result = MCPResponse.Error(ticket.ErrorMessage, ticket.ErrorCode, true,
                new Dictionary<string, object>
                {
                    { "totalQueued", totalQueued }, { "agentQueued", agentQueued },
                    { "maxTotalQueued", MaxTotalQueuedRequests },
                    { "maxPerAgentQueued", MaxQueuedRequestsPerAgent },
                });
            _completedTickets[ticket.TicketId] = ticket;
            PersistTicketSnapshotsLocked();
            return ticket;
        }

        private static Dictionary<string, object> OwnedTicketToDict(RequestTicket ticket, string agentId,
            bool enforceOwnership)
        {
            if (enforceOwnership && !string.Equals(ticket.AgentId, agentId, StringComparison.Ordinal))
                return MCPResponse.Error("Ticket belongs to another agent.", "ticket_owner_mismatch");
            return TicketToDict(ticket);
        }

        private static void SignalWaitersLocked(long ticketId)
        {
            if (!_waiters.TryGetValue(ticketId, out var waiters))
                return;
            foreach (var waiter in waiters.ToArray())
                waiter.Set();
        }

        /// <summary>
        /// Fair round-robin dequeue. Returns 1 write OR a caller-bounded read batch.
        /// Must be called under _queueLock.
        /// </summary>
        private static List<RequestTicket> DequeueNextBatch(bool allowWrites, int maxRequests)
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
                if (allowWrites == false)
                    return null;

                // Single write request
                _agentQueues[firstAgent].Dequeue();
                batch.Add(first);
            }
            else
            {
                // Batch reads across agents (round-robin)
                int collected = 0;
                int scanIdx = (_rrIndex - 1 + _rrOrder.Count) % _rrOrder.Count;

                int readBatchSize = Math.Min(MaxReadBatchSize, maxRequests);
                for (int pass = 0; collected < readBatchSize && pass < _rrOrder.Count * readBatchSize; pass++)
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

        private static bool HasExecutingWriteLocked()
        {
            foreach (var ticket in _executingTickets.Values)
            {
                if (!IsReadOperation(ticket.ActionName))
                    return true;
            }

            return false;
        }

        private static bool IsReadOperation(string actionName)
        {
            return !string.IsNullOrEmpty(actionName) && MCPToolMetadata.IsRouteReadOnly(actionName);
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

        private static bool RunCleanup()
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
                    SignalWaitersLocked(ticket.TicketId);
                }

                if (kill.Count == 0 && staleExecuting.Count == 0)
                    return false;

                PersistTicketSnapshotsLocked();
                return true;
            }
        }

        private static void EnsurePersistentSnapshotsLoaded()
        {
            lock (_queueLock)
            {
                if (_persistentSnapshotsLoaded)
                    return;

                _persistentSnapshotsLoaded = true;
                string json = ReadPersistentTicketSnapshots();
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
                    if (TryRestoreEditorIdleWait(snapshot, out var restoredTicket) ||
                        TryRestorePersistentRequest(snapshot, out restoredTicket))
                    {
                        if (restoredTicket.Status == RequestStatus.Queued)
                            EnqueueRestoredTicketLocked(restoredTicket);
                        else
                            _completedTickets[restoredTicket.TicketId] = restoredTicket;
                    }
                    else
                    {
                        _reloadedTicketSnapshots[ticketId] = BuildReloadedSnapshot(snapshot);
                    }
                }

                if (maxTicketId > _nextTicketId)
                    Interlocked.Exchange(ref _nextTicketId, maxTicketId);
            }
        }

        private static bool TryRestoreEditorIdleWait(Dictionary<string, object> snapshot,
            out RequestTicket ticket)
        {
            ticket = null;
            string actionName = GetString(snapshot, "actionName");
            if (actionName != "wait/editor-idle" ||
                !TryGetLong(snapshot, "ticketId", out long ticketId))
                return false;

            var persistentArguments = snapshot.TryGetValue("persistentArguments", out var rawArguments)
                ? MCPResponse.ToDictionary(rawArguments)
                : null;
            string statusText = GetString(snapshot, "status");
            if (!Enum.TryParse(statusText, out RequestStatus previousStatus))
                return false;

            DateTime submittedAt = GetDateTime(snapshot, "submittedAt", DateTime.UtcNow);
            DateTime? completedAt = TryGetDateTime(snapshot, "completedAt", out var parsedCompletedAt)
                ? parsedCompletedAt
                : null;
            ticket = new RequestTicket
            {
                TicketId = ticketId,
                AgentId = GetString(snapshot, "agentId", "anonymous"),
                ActionName = actionName,
                SubmittedAt = submittedAt,
                CompletedAt = completedAt,
                QueuePosition = GetInt(snapshot, "queuePosition", 0),
                ErrorMessage = GetString(snapshot, "errorMessage"),
                ErrorCode = GetString(snapshot, "errorCode"),
                Retryable = GetBool(snapshot, "retryable", false),
                RequestKey = GetString(snapshot, "requestKey"),
                PersistentArguments = persistentArguments,
                ResumeCount = GetInt(snapshot, "resumeCount", 0),
                Progress = snapshot.TryGetValue("progress", out var progress) ? progress : null,
            };

            if (previousStatus == RequestStatus.Completed || previousStatus == RequestStatus.Failed ||
                previousStatus == RequestStatus.TimedOut)
            {
                if (!snapshot.TryGetValue("result", out var result))
                {
                    ticket = null;
                    return false;
                }
                ticket.Status = previousStatus;
                ticket.Result = result;
                return true;
            }

            if ((previousStatus != RequestStatus.Queued && previousStatus != RequestStatus.Executing) ||
                persistentArguments == null)
            {
                ticket = null;
                return false;
            }

            int originalTimeoutMs = Math.Max(1, GetInt(persistentArguments, "timeoutMs", 30000));
            DateTime deadlineUtc = GetDateTime(persistentArguments, "_deadlineUtc",
                submittedAt.AddMilliseconds(originalTimeoutMs));
            int remainingTimeoutMs = (int)Math.Max(1,
                Math.Min(int.MaxValue, (deadlineUtc - DateTime.UtcNow).TotalMilliseconds));
            var resumedArguments = new Dictionary<string, object>(persistentArguments)
            {
                ["timeoutMs"] = remainingTimeoutMs,
                ["_resumeCount"] = ticket.ResumeCount + 1,
                ["_deadlineUtc"] = deadlineUtc.ToString("O"),
            };
            ticket.Status = RequestStatus.Queued;
            ticket.CompletedAt = null;
            ticket.Result = null;
            ticket.Progress = null;
            ticket.ErrorMessage = null;
            ticket.ErrorCode = null;
            ticket.Retryable = false;
            ticket.PersistentArguments = resumedArguments;
            ticket.ResumeCount++;
            if (string.IsNullOrEmpty(ticket.RequestKey))
                ticket.RequestKey = MCPEditorCommands.BuildWaitForIdleRequestKey(persistentArguments);
            ticket.ProgressiveDeferredAction = (resolve, _) =>
                MCPEditorCommands.WaitForIdle(resumedArguments, resolve);
            return true;
        }

        private static bool TryRestorePersistentRequest(Dictionary<string, object> snapshot,
            out RequestTicket ticket)
        {
            ticket = null;
            string actionName = GetString(snapshot, "actionName");
            string persistentBody = GetString(snapshot, "persistentBody", null);
            if (string.IsNullOrEmpty(actionName) || persistentBody == null ||
                !TryGetLong(snapshot, "ticketId", out long ticketId))
                return false;

            if (!Enum.TryParse(GetString(snapshot, "status"), out RequestStatus previousStatus))
                return false;
            ticket = new RequestTicket
            {
                TicketId = ticketId,
                AgentId = GetString(snapshot, "agentId", "anonymous"),
                ActionName = actionName,
                SubmittedAt = GetDateTime(snapshot, "submittedAt", DateTime.UtcNow),
                CompletedAt = TryGetDateTime(snapshot, "completedAt", out var completedAt) ? completedAt : (DateTime?)null,
                QueuePosition = GetInt(snapshot, "queuePosition", 0),
                ErrorMessage = GetString(snapshot, "errorMessage"),
                ErrorCode = GetString(snapshot, "errorCode"),
                Retryable = GetBool(snapshot, "retryable", false),
                RequestKey = GetString(snapshot, "requestKey"),
                PersistentBody = persistentBody,
                PersistentMethod = GetString(snapshot, "persistentMethod", "POST"),
                IsReadOnly = GetBool(snapshot, "isReadOnly", MCPToolMetadata.IsRouteReadOnly(actionName)),
                ResumeCount = GetInt(snapshot, "resumeCount", 0),
                Progress = snapshot.TryGetValue("progress", out object progress) ? progress : null,
            };

            if (previousStatus == RequestStatus.Completed || previousStatus == RequestStatus.Failed ||
                previousStatus == RequestStatus.TimedOut || previousStatus == RequestStatus.Canceled ||
                previousStatus == RequestStatus.UncertainAfterReload)
            {
                ticket.Status = previousStatus;
                ticket.Result = snapshot.TryGetValue("result", out object result) ? result : null;
                return true;
            }

            if (previousStatus != RequestStatus.Queued && previousStatus != RequestStatus.Executing)
                return false;

            if (previousStatus == RequestStatus.Executing && !ticket.IsReadOnly &&
                !IsReloadResumableMutation(actionName))
            {
                ticket.Status = RequestStatus.UncertainAfterReload;
                ticket.CompletedAt = DateTime.UtcNow;
                ticket.ErrorMessage =
                    "A mutating request crossed a Unity domain reload after execution began. Inspect its target before retrying.";
                ticket.ErrorCode = "mutation_outcome_uncertain_after_reload";
                ticket.Retryable = false;
                ticket.Result = MCPResponse.Error(ticket.ErrorMessage, ticket.ErrorCode, false,
                    new Dictionary<string, object>
                    {
                        { "actionName", actionName }, { "requestKey", ticket.RequestKey ?? "" },
                        { "requiresReconciliation", true },
                    });
                return true;
            }

            ticket.Status = RequestStatus.Queued;
            ticket.CompletedAt = null;
            ticket.ErrorMessage = null;
            ticket.ErrorCode = null;
            ticket.Retryable = false;
            ticket.ResumeCount++;
            var resumedTicket = ticket;
            if (MCPBridgeServer.IsDeferredRoute(resumedTicket.ActionName))
            {
                resumedTicket.ProgressiveDeferredAction = (resolve, progressCallback) =>
                    MCPBridgeServer.ExecutePersistedDeferredRoute(resumedTicket.ActionName,
                        resumedTicket.PersistentBody,
                        resolve, progressCallback);
            }
            else
            {
                resumedTicket.Action = () => MCPBridgeServer.ExecutePersistedRoute(resumedTicket.ActionName,
                    resumedTicket.PersistentMethod, resumedTicket.PersistentBody);
            }
            return true;
        }

        private static bool IsReloadResumableMutation(string actionName)
        {
            // Asset refresh persists its own job before entering AssetDatabase.Refresh,
            // and MCPAssetRefreshWorkflow.Start reuses that job for the same request ID.
            // Play Mode transitions are explicit target states (never toggles), so replaying
            // the persisted deferred request after a domain reload is also idempotent.
            return actionName == "asset/refresh" || actionName == "editor/play-mode";
        }

        private static void EnqueueRestoredTicketLocked(RequestTicket ticket)
        {
            if (!_agentQueues.TryGetValue(ticket.AgentId, out var queue))
            {
                queue = new Queue<RequestTicket>();
                _agentQueues[ticket.AgentId] = queue;
                _rrOrder.Add(ticket.AgentId);
            }
            ticket.QueuePosition = queue.Count;
            queue.Enqueue(ticket);
            EnsureSession(ticket.AgentId).LogAction(ticket.ActionName + " (resumed)");
        }

        private static Dictionary<string, object> BuildReloadedSnapshot(Dictionary<string, object> snapshot)
        {
            var restored = new Dictionary<string, object>(snapshot);
            string previousStatus = restored.TryGetValue("status", out var status) ? status?.ToString() : "";
            restored["previousStatus"] = previousStatus;
            restored["status"] = RequestStatus.UncertainAfterReload.ToString();
            restored["success"] = false;
            restored["error"] = "This legacy ticket had no persisted request body, so its outcome cannot be reconstructed after a Unity domain reload.";
            restored["message"] = restored["error"];
            restored["errorCode"] = "legacy_ticket_outcome_uncertain";
            restored["retryable"] = false;
            restored["result"] = MCPResponse.Error(restored["error"].ToString(),
                "legacy_ticket_outcome_uncertain", false);
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

            WritePersistentTicketSnapshots(MiniJson.Serialize(snapshots));
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

            if (ticket.Progress != null)
                snapshot["progress"] = ticket.Progress;

            if (!string.IsNullOrEmpty(ticket.RequestKey))
                snapshot["requestKey"] = ticket.RequestKey;
            if (ticket.PersistentArguments != null)
                snapshot["persistentArguments"] = ticket.PersistentArguments;
            if (ticket.PersistentBody != null)
                snapshot["persistentBody"] = ticket.PersistentBody;
            if (!string.IsNullOrEmpty(ticket.PersistentMethod))
                snapshot["persistentMethod"] = ticket.PersistentMethod;
            snapshot["isReadOnly"] = ticket.IsReadOnly;
            if (ticket.ResumeCount > 0)
                snapshot["resumeCount"] = ticket.ResumeCount;
            if (ticket.Result != null &&
                (ticket.Status == RequestStatus.Completed || ticket.Status == RequestStatus.Failed ||
                 ticket.Status == RequestStatus.TimedOut || ticket.Status == RequestStatus.Canceled ||
                 ticket.Status == RequestStatus.UncertainAfterReload))
                snapshot["result"] = ticket.Result;

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

        private static int GetInt(Dictionary<string, object> dictionary, string key, int defaultValue)
        {
            return dictionary != null && dictionary.TryGetValue(key, out var raw) && raw != null &&
                   int.TryParse(raw.ToString(), out int value)
                ? value
                : defaultValue;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key,
            string defaultValue = "")
        {
            return dictionary != null && dictionary.TryGetValue(key, out var raw) && raw != null
                ? raw.ToString()
                : defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> dictionary, string key, bool defaultValue)
        {
            if (dictionary == null || !dictionary.TryGetValue(key, out var raw) || raw == null)
                return defaultValue;
            if (raw is bool value)
                return value;
            return bool.TryParse(raw.ToString(), out value) ? value : defaultValue;
        }

        private static DateTime GetDateTime(Dictionary<string, object> dictionary, string key,
            DateTime defaultValue)
        {
            return TryGetDateTime(dictionary, key, out var value) ? value : defaultValue;
        }

        private static bool TryGetDateTime(Dictionary<string, object> dictionary, string key,
            out DateTime value)
        {
            value = default;
            return dictionary != null && dictionary.TryGetValue(key, out var raw) && raw != null &&
                   DateTime.TryParse(raw.ToString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out value);
        }

        private static string ReadPersistentTicketSnapshots()
        {
            try
            {
                string path = GetPersistentTicketSnapshotPath();
                if (TryReadValidSnapshotJson(path, out string json))
                    return json;
                string backupPath = GetPersistentTicketSnapshotBackupPath();
                if (TryReadValidSnapshotJson(backupPath, out json))
                {
                    Debug.LogWarning("[Unity MCP Queue] Recovered ticket snapshots from the atomic backup file.");
                    return json;
                }
                return "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Unity MCP Queue] Failed to read ticket snapshots: {ex.Message}");
                return "";
            }
        }

        private static void WritePersistentTicketSnapshots(string json)
        {
            try
            {
                string path = GetPersistentTicketSnapshotPath();
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                WriteTextAtomically(path, json ?? "[]");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Unity MCP Queue] Failed to persist ticket snapshots: {ex.Message}");
            }
        }

        private static bool TryReadValidSnapshotJson(string path, out string json)
        {
            json = "";
            if (!File.Exists(path))
                return false;
            try
            {
                json = File.ReadAllText(path);
                return MiniJson.Deserialize(json) is List<object>;
            }
            catch
            {
                json = "";
                return false;
            }
        }

        private static void WriteTextAtomically(string path, string contents)
        {
            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";
            if (File.Exists(tempPath))
                File.Delete(tempPath);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(true);
            }

            if (!File.Exists(path))
            {
                File.Move(tempPath, path);
                return;
            }

            if (File.Exists(backupPath))
                File.Delete(backupPath);
            try
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(path, backupPath, true);
                File.Copy(tempPath, path, true);
                File.Delete(tempPath);
            }
        }

        private static string GetPersistentTicketSnapshotPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "Library", "UnityMCP",
                PersistentTicketSnapshotFileName);
        }

        private static string GetPersistentTicketSnapshotBackupPath()
        {
            return GetPersistentTicketSnapshotPath() + ".bak";
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

            if (t.Progress != null)
                dict["progress"] = t.Progress;

            if (t.CompletedAt.HasValue)
                dict["completedAt"] = t.CompletedAt.Value.ToString("O");

            if (t.Status == RequestStatus.Failed ||
                t.Status == RequestStatus.TimedOut ||
                t.Status == RequestStatus.Canceled ||
                t.Status == RequestStatus.UncertainAfterReload)
            {
                string message = string.IsNullOrEmpty(t.ErrorMessage)
                    ? "Queue processing failed."
                    : t.ErrorMessage;
                dict["success"] = false;
                dict["error"] = message;
                dict["message"] = message;
            }

            // Include result for completed tickets
            if (t.Status == RequestStatus.Completed || t.Status == RequestStatus.Failed ||
                t.Status == RequestStatus.TimedOut || t.Status == RequestStatus.Canceled ||
                t.Status == RequestStatus.UncertainAfterReload)
                dict["result"] = t.Result;

            return dict;
        }
    }
}
