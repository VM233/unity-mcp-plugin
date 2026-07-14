# Changelog

All notable changes to this package will be documented in this file.

## [3.2.5] - 2026-07-14

- Blocked Git package updates while a package-test workflow is non-terminal, preventing its exact manifest restoration from overwriting a concurrent package revision change.
- Made the deferred prefab refresh regression assert the plugin's missing-type refresh scheduling directly, avoiding false failures from prefab loading importing unrelated files on Unity 6.4.
- Failed filtered Unity Test Runner jobs with `no_tests_matched` when zero tests are selected, eliminating false-success package-test runs.

## [3.2.4] - 2026-07-14

- Rebuilt terminal Unity Test Runner details from the final result tree and persisted failure diagnostics across reloads, so package-test failures retain their names, messages, and stacks after manifest restoration.

## [3.2.3] - 2026-07-14

- Limited parameter-level `wait/editor-idle` coalescing to active tickets, preventing a later wait from reusing a stale completed result while retaining completed-ticket reuse for transport idempotency keys.

## [3.2.2] - 2026-07-14

- Normalized unbound and wrong-project rejection payloads with stable `target_project_required` and `wrong_unity_project` error codes.

## [3.2.1] - 2026-07-14

- Fixed package-test compilation by keeping regression access to internal refresh and localization types reflection-based.
- Explicitly classified route/tool metadata endpoints as read-only under conservative target-binding defaults.

## [3.2.0] - 2026-07-14

- Added first-class asset folder creation, generic asset copy, incoming/outgoing dependency graphs, and rollback-capable cross-asset transactions.
- Added structured UXML and USS editing plus rollback-capable multi-file UI Toolkit authoring transactions.
- Added first-class Package Manager add, remove, list, and paginated search routes without blocking the Unity main thread.
- Added owner-scoped persistent job history for asset refresh, Player Build, Unity Test Runner, and package-test workflows.
- Added queue cancellation, stable request idempotency, per-agent/global capacity limits, metadata-driven read scheduling, and general domain-reload restoration. Interrupted reads resume; interrupted mutations become explicit non-retryable `UncertainAfterReload` results.
- Made `asset/refresh` reuse its persisted job and queue ticket for the same owner/request across a domain reload, while persisting `Executing` before every Unity action so unrelated mutations are never replayed as if they had not started.
- Replaced runtime C# source parsing with an explicit route registry guarded by regression tests, and filtered optional Localization, Shader Graph, Amplify, and UMA routes by live capability detection.
- Made project-tool first-class exposure explicit through `MCPProjectToolAttribute.FirstClass`; unselected project tools remain available through paginated discovery and `project-tools/execute`.
- Required mutating requests to bind to an expected Unity project, with the MCP server automatically forwarding selected-instance identity and stable idempotency keys.
- Standardized pagination metadata for large asset, package, project-tool, dependency, job, test, and metadata responses.

## [3.1.22] - 2026-07-14

- Ordered targeted asset refreshes by known AssetDatabase dependencies and completed each import synchronously, preventing dependent UXML imports from observing stale USS timestamps in SourceAssetDB.

## [3.1.21] - 2026-07-14

- Made `screenshot/game` capture the Game View's completed render texture directly while Play Mode is paused, without advancing simulation ticks or waiting for a new rendered frame.
- Added paused-frame vertical orientation correction, supersized output support, PNG readback validation, and regression coverage for render-texture capture.

## [3.1.20] - 2026-07-14

- Made `wait/editor-idle` tickets reload-resumable with the original ticket ID, remaining deadline, persisted terminal result, and explicit resume diagnostics.
- Coalesced duplicate active editor-idle waits across reconnecting agents and allowed multiple synchronous callers to observe the same ticket safely.
- Persisted queue snapshots with atomic replacement and a validated backup, preventing a domain reload from reading a partial ticket file.
- Classified editor-idle waits as non-mutating queue work so they no longer block unrelated reads while waiting for Unity to settle.

## [3.1.19] - 2026-07-14

- Added decoded-pixel and file-byte duplicate detection to batch `asset/import`, including project/folder scopes, skip/error/report policies, existing-asset matches, and within-batch matches.
- Added the first-class read-only `texture/find-duplicates` project image audit tool, with bounded folder, extension, asset, and group controls.

## [3.1.18] - 2026-07-14

- Use Unity's indexed `TypeCache` for project-tool discovery instead of scanning every loaded assembly and type, preventing metadata requests and regression tests from timing out in large projects or after runtime code compilation.

## [3.1.17] - 2026-07-13

- Return the same stable dictionary result shape for `asset/import` preflight failures as for completed batch results.

## [3.1.16] - 2026-07-13

- Upgraded `asset/import` to preflight and import up to 500 assets with shared TextureImporter defaults, immediate or frame-batched execution, per-item results, overwrite protection, and rollback.
- Removed the single-file `sourcePath`/`destinationPath` request shape in favor of the canonical `imports` collection and shared `execution` model.

## [3.1.15] - 2026-07-13

- Keep AssetDatabase refresh jobs non-terminal until compilation, asset updating, and a stable idle window have completed, so `succeeded` no longer races a delayed domain reload.

## [3.1.14] - 2026-07-13

- Exposed `build/run-test` as a first-class persistent Player Build job with `build/get-job` polling, so normal builds no longer fall through the queue's 30-second synchronous timeout.
- Changed `asset/refresh` into a reload-safe persistent job with `asset/get-refresh-job` polling and removed the duplicate targeted-import pass after a full external-change reconciliation.
- Documented that a successful Player Build report is authoritative and does not require a follow-up forced AssetDatabase refresh.

## [3.1.13] - 2026-07-13

- Made package-test polling actively restore the workflow update pump after domain reloads so waiting, timeout, test execution, and manifest restoration cannot stall behind a lost editor callback.

## [3.1.12] - 2026-07-13

- Added explicit `MutatesRuntime` metadata for project tools so runtime state changes can be exposed as first-class tools without misclassifying them as asset edits.

## [3.1.11] - 2026-07-13

- Exposed Animator transition inspection, state updates, transition updates, and state connection workflows as first-class MCP tools.

## [3.1.10] - 2026-07-13

- Exposed texture inspection and sprite-import preset application as first-class MCP tools alongside external asset import.

## [3.1.9] - 2026-07-12

- Allowed primitive JSON values in prefab, serialized-object, and localization value schemas.
- Added first-class component property editing and support for inherited `Behaviour.enabled`.
- Allowed an explicit empty prefab path to reference a prefab root object or component.
- Reconciled external AssetDatabase changes before ordered targeted imports to avoid stale timestamp warnings.

## [3.1.8] - 2026-07-11

- Validate nested serialized component-reference migration with a built-in runtime component instead of an Editor-only test component that Unity cannot attach to prefabs.

## [3.1.7] - 2026-07-11

- Preserve direct, nested managed-reference, and exposed references when `prefab-asset/move-component` replaces the source component with its destination copy.

## [3.1.6] - 2026-07-11

- Limit MCP queue processing to one request per Editor update and pause processing during compilation, asset updates, and a short post-reload stabilization window, preventing reconnect backlogs from triggering long `MCPBridgeServer.OnEditorUpdate` stalls.
- Remove the redundant unbounded legacy main-thread queue; synchronous HTTP requests now rely on the existing fair ticket queue for main-thread execution.

## [3.1.5] - 2026-07-11

- Use Roslyn `Preview` for dynamic code because Unity 6.4 classifies `is not` patterns as preview syntax.
- Complete external asset reconciliation synchronously before `asset/refresh` returns success.

## [3.1.4] - 2026-07-11

- Compile `editor/execute-code` with the latest language version supported by Unity's bundled Roslyn, including `is not` patterns.
- Targeted `asset/refresh` calls now reconcile external file creation and deletion by default, preventing stale deleted scripts from remaining in Unity's compiler source list.

## [3.1.3] - 2026-07-11

- Corrected the prefab hierarchy identity-transform regression test to validate the returned root node shape.

## [3.1.2] - 2026-07-11

- Omit identity Transform values from GameObject, scene hierarchy, prefab hierarchy/find, terrain, lighting, prefab instantiation, and physics overlap responses. Zero positions, identity rotations, and unit scales are no longer serialized.
- Package test workflows now retain summaries and failed/inconclusive details by default instead of every passing test result. Full passing details remain available from `testing/get-job` with `includeDetails=true`.
- Remove compatibility aliases from tool schemas and use canonical request fields only.
- Omit redundant MCP annotations whose values are false and remove annotation titles that duplicate tool names.

## [3.0.0] - 2026-07-11

- Fixed prefab transaction property writes rejecting serialized array-size paths such as `items.Array.size` with `Cannot set property type: ArraySize`.
- Fixed prefab batch/transaction edits unconditionally refreshing assets before checking already-loaded component types. Missing types now produce a retryable response before a delayed refresh, so a script-triggered Domain Reload does not cut off the active MCP response.
- Fixed prefab asset edits rewriting untouched YAML whitespace or serializing unrelated default component fields.
- Replaced separate batch routes with a shared `execution` object. `execution.mode` supports `auto`, `immediate`, and `batched`, with common per-frame, timeout, and error-continuation controls.
- Removed `prefab-asset/batch-edit`, `asset/move-batch`, `component/batch-wire`, and `localization/upsert-entries`. Their multi-operation behavior now lives on `prefab-asset/transaction-edit`, `asset/move`, `component/set-reference`, and `localization/upsert-entry`.

### Added
- **Optional Unity Localization tools** - When `com.unity.localization` is installed, first-class tools expose Locale management, String/Asset Table Collections, localized entry CRUD, Smart String flags and persistent variables, validation, and Localization Settings. The integration assembly and tool metadata stay hidden when the package is absent.
- **Completed visual capture results** - `screenshot/game` now waits for a stable, decodable PNG and reports dimensions, byte size, elapsed time, and readiness instead of returning before the next frame writes the file.
- **Readable project-tool names** - project tools expose compact `unity_pt_*` names capped for MCP clients, retain the legacy name in metadata, and can opt into an explicit `MCPProjectToolAttribute.ShortName`.
- **First-class testing tools** - Test discovery, test execution, job polling, and persistent Git package self-tests now expose concrete tools and schemas.
- **Persistent package test workflow** - `testing/run-package-tests` backs up `Packages/manifest.json`, enables the requested package in `testables`, runs its test assembly after reload, then restores the original manifest bytes.
- **Atomic prefab component moves** - `prefab-asset/move-component` / `unity_prefab_asset_move_component` copies a component to another GameObject, verifies the destination, removes the source, and saves once while preserving serialized data.
- **Compact scene component filtering** - `scene/hierarchy` accepts `componentType`, `nameContains`, `pathContains`, and `maxResults` to return compact flat matches instead of serializing the entire scene tree.
- **Unified asset moves** - `asset/move` accepts a `moves` array, preflights the complete request, preserves GUID/meta state, rolls back completed moves on stop-on-error failures, and supports immediate or frame-batched execution.
- **Project tool selection hints** - `MCPProjectToolAttribute` can now declare `ReadOnly`, `MutatesAssets`, `Dangerous`, `LongRunning`, `MayReloadDomain`, and `RequiresPlayMode`; `_meta/tools` also infers common read-only `get/list/*summary` project tools and mutating asset/prefab tools when hints are not explicit.
- **Tool metadata profiles** - `_meta/tools` now uses a single `ToolProfile` registry for first-class/fallback/lazy exposure plus `readOnly`, `mutatesAssets`, `dangerous`, `longRunning`, `mayReloadDomain`, and `requiresPlayMode` hints.
- **Project tool input validation** - project tools declared with `MCPProjectToolAttribute.InputSchemaJson` now validate schema shape at discovery time and validate required fields, primitive JSON types, and `additionalProperties=false` before execution.
- **Reload-aware queue snapshots** - queue tickets persist small status snapshots through Unity domain reloads. Polling a lost ticket now returns a retryable `ticket_lost_after_reload` response instead of a generic not-found result.
- **Concrete tool surface cleanup** - `asset/refresh`, serialized-object get/set, compilation errors, common prefab-asset read/write routes, and clearer prefab instantiation aliases are now first-class in `_meta/tools`. `advanced/execute` remains available but is advertised as a fallback instead of a preferred entrypoint.
- **Prefab transaction operation schemas** - `prefab-asset/transaction-edit` exposes operation-level schemas for `addComponent`, `setProperty`, `setReference`, `addGameObject`, `instantiatePrefab`, `removeComponent`, `removeGameObject`, and `moveGameObject`, plus the shared execution schema.
- **Multi-editor project routing safety** — new `instance/current`, `instance/list`, `instance/resolve`, and `instance/assert-project` routes expose the shared Unity MCP instance registry so clients can resolve the correct Editor by `projectPath` before sending commands. Requests can also include `expectedProjectPath` / `targetProjectPath` / `unityProjectPath` or the `X-UnityMCP-Expected-Project-Path` header; if a command reaches the wrong Unity project, the bridge returns `wrong_unity_project` before executing Unity API work.
- **First-class project tools** — `MCPProjectToolAttribute` now supports `InputSchemaJson`, `project-tools/list` returns schemas and direct routes, and `_meta/tools` exposes each valid project tool as a concrete `unity_project_tool_*` tool routed through `project-tools/call/<toolName>`.
- **First-class route metadata** — stable routes advertised in the README now include `firstClass=true` in `_meta/tools`, so MCP clients can expose concrete tools with route-owned schemas and descriptions instead of routing them through the generic advanced entry.

### Changed
- **Compact targeted UI asset inspection** - `uitoolkit/asset-inspect` names queries omit the unrelated general element list by default, share one result budget, and return only relevant USS classes unless full output is requested.
- **Token-bounded metadata** - `_meta/tools` now defaults to compact first-class metadata without schemas, returns at most 50 tools per page, and requires explicit flags for schemas or legacy duplicate collections. Full catalogs support category filters and pagination.
- **Bounded query responses** - scene and prefab hierarchies, Console queries, test discovery/results, SerializedObject reads, and execute-code serialization now use conservative defaults with pagination or explicit truncation metadata. Console stacks and test stacks are opt-in.
- **Lean first-class surface** - duplicate prefab aliases and low-frequency visual, animation, build, package, and queue routes remain available through the advanced catalog instead of occupying every MCP `tools/list` response.
- **Project tool exposure** - read-only and asset-mutating project tools remain concrete; runtime mutation commands are discovered through `project-tools/list` and called through `project-tools/execute` instead of all occupying `tools/list`.
- **Prefab diff summaries** - prefab mutations return summary diffs by default; callers can explicitly request `minimal` or `full` lines.

### Fixed
- **Execute-code assembly context safety** - dynamic code that references Unity, project, or package assemblies now skips the isolated AppDomain and runs against Unity's loaded assembly context, preventing missing dependency failures and unsafe cross-domain asset serialization. Pure framework-only code remains unloadable through AppDomain isolation.
- **UI Builder preview evidence** - `uitoolkit/builder-preview` now waits for the requested UXML document and a laid-out canvas, focuses and repaints across stable frames, restores previous focus, and rejects failed or visually blank captures instead of reporting unconditional success.
- **Editor-window DPI cropping** - docked EditorWindow captures prefer raw screen-pixel coordinates, use explicit local/scaled fallbacks, and report the selected coordinate mode plus center-content diagnostics.
- **Execute-code UI Toolkit and diagnostics** - dynamic code includes `UnityEngine.UIElements`, accepts additional namespace imports, maps compiler diagnostics back to user-code line numbers, and uses a collectible `AssemblyLoadContext` when available instead of permanently accumulating dynamic assemblies.
- **Play-mode screenshot contract** - `screenshot/game` now rejects EditMode immediately with `requires_play_mode` instead of waiting for a frame that Unity will never render.
- **Package-test failure evidence** - persistent package test workflows now save paginated test details and stack traces before restoring `manifest.json`, so failures remain diagnosable after the test assembly unloads.
- **Direct collection arguments** - UI Toolkit asset inspection accepts arrays and other enumerable values in direct C# calls as well as JSON `List<object>` inputs.
- **Prefab YAML block ordering** - prefab saves preserve the original order of surviving Unity YAML object blocks, append only new blocks, remove deleted blocks, validate block equivalence, and continue stripping trailing whitespace.
- **Test Runner completion** - jobs finalize from completed leaf results when Unity's root `RunFinished` callback arrives late; an unfocused Editor is reported as informational state instead of a blocking reason.
- **Prefab mutation rollback and YAML diffs** - failed `prefab-asset/add-gameobject` and component moves restore the original prefab bytes; successful prefab saves remove trailing YAML whitespace; line diffs now use a real edit script and report complete added/removed totals independently from truncation.
- **Execute-code structured results** - nested arrays, lists, dictionaries, anonymous objects, and Unity values are serialized recursively instead of degrading to CLR type names such as `System.String[]`.
- **SerializeReference array writes** - `serialized-object/get` now reports `$managedReferenceType`, and `serialized-object/set` can instantiate new managed-reference elements from that type or infer it from a homogeneous existing list. Unsupported writes now return a structured error without a Unity Console exception.
- **Prefab transaction reliability** - `prefab-asset/transaction-edit` applies operations according to the shared execution policy, returns progress snapshots and structured timeout failures, and reports explicit persistence state (`saved`, `saveAttempted`, `partialPersistedKnown`, `persistedState`).
- **Serialized complex fields** - component property read/write now expands and accepts serialized arrays/lists plus generic child objects instead of reporting complex list fields only as `Generic`.
- **Deferred write exclusivity** - multi-frame write requests now block later writes from leaving the queue until the active write completes, preventing interleaved asset edits while a deferred prefab batch is still applying.
- **Queue failure status details** - `queue/status` now includes top-level `success=false`, `error`, and `message` fields for failed tickets so MCP clients can preserve validation and project-tool errors.
- **Editor idle diagnostics** - `editor/state` now includes `isUpdating`, `isChangingPlayMode`, and `isPlayingOrWillChangePlaymode` so the MCP server can distinguish true Editor busyness from queue polling false negatives.
- **Package meta lint false positives** - `packages/lint-metas` now skips hidden dotfiles and dot directories such as `.gitattributes`, `.gitignore`, and `.github`, matching Unity's non-imported file behavior.
- **Error result consistency** - bridge and queue paths now normalize error payloads with `success=false`, `errorCode`, `message`, and `retryable` while keeping existing successful result payloads backward-compatible.
- **Long direct calls** - synchronous direct calls that exceed the immediate wait window now return a retryable response with a `ticketId` and `pollRoute` while the queued Unity operation continues in the background.
- **Deferred route direct-call timeout** — direct calls to deferred routes such as `advanced/execute` now wait on a deferred queue ticket instead of wrapping another main-thread wait, preventing 30s timeouts when the route is used as the stable generic entry.

## [2.32.0] - 2026-06-02

### Added
- **`screenshot/editor-window` command** — `MCPScreenshotCommands.CaptureEditorWindow` captures any EditorWindow (Inspector, Project, Console, custom windows) to a PNG via the Win32 `PrintWindow` API (`PW_RENDERFULLCONTENT`), occlusion-proof (the window renders itself offscreen — no raise or focus-steal). Docked windows are captured by PrintWindowing the main window and cropping the panel rect; floating windows by resolving their own HWND (exact title match) and capturing the whole window. Defaults to `Assets/Screenshots/`, honours any user-chosen `.png` path; bounds dimensions against `SystemInfo.maxTextureSize`, all GDI handles + the `Texture2D` released in `try/finally`. **Windows editor only** (`#if UNITY_EDITOR_WIN`) — returns a clear unsupported-platform error on macOS/Linux (no `PrintWindow` equivalent); use `screenshot/scene` / `screenshot/game` (camera-based) there. Companion to the `unity-mcp-server` 2.30.0 change.

### Changed
- **Welcome window reworked into a modular, themed system** — the single-file `Editor/MCPWelcomeWindow.cs` is replaced by `1-Scripts/Editor/WelcomeWindow/` (own assembly `UnityMCP.Editor.Welcome`, namespace `UnityMCP.Editor.Welcome`): a USS theme, Welcome + Studio tabs, auto-open on first load with per-project detection, a config-driven content seam (custom sections / buttons, cross-sell entries via `welcome.gen.json`), a devlog fetcher, and bundled icons.

## [2.31.2] - 2026-05-21

### Changed
- **Settings panel grouped into labelled sections** — the Dashboard's *Settings* foldout now has three bold sub-headers (**General**, **Port**, **Multiplayer Play Mode (MPPM)**) instead of an unlabelled flat list. The *Start on Virtual Players* toggle is now under the explicit **MPPM** header so its scope is clear, and it was moved below the Port settings. UI-only change, no behaviour difference.

## [2.31.1] - 2026-05-21

### Fixed
- **MPPM scenario commands now work on MPPM 2.0 (Unity 6)** — the 2.31.0 Unity 6 port resolved the scenario types under the wrong names. In MPPM 2.0 the scenario "config" ScriptableObject was renamed `OrchestratedScenario` (from `ScenarioConfig`) and the status struct `ScenarioStatusData` (from `ScenarioStatus`); `MCPScenarioCommands` now resolves both. `create_scenario` no longer requires the removed `RemoteInstanceDescription` type (remote instances were dropped in MPPM 2.0), and `list_scenarios` reads instance counts from `OrchestratedScenario`'s fields. All MPPM tools verified end-to-end on Unity 6000.5.0b8 + MPPM 2.0.2.

## [2.31.0] - 2026-05-21

### Added
- **MPPM Virtual Player management** — new commands `mppm/list-players`, `mppm/activate-player`, `mppm/deactivate-player` to list and activate/deactivate Multiplayer Play Mode virtual players by 1-based index.
- **`scenario/create`** — create an MPPM `ScenarioConfig` asset programmatically (one Main Editor instance + N Virtual Editor instances with configurable Host/Client/Server roles).

### Changed
- **MPPM scenario commands now work on Unity 6** — `MCPScenarioCommands` resolves the MPPM scenario types from both the legacy package assembly (`Unity.Multiplayer.PlayMode.Scenarios.Editor`, pre-Unity-6) and the built-in `UnityEditor.MultiplayerModule` introduced in Unity 6; previously all `mppm/*` commands returned "MPPM is not installed" on Unity 6. `scenario/start` / `scenario/stop` also enter/exit Play mode so virtual-player launch hooks fire.

## [2.30.0] - 2026-05-21

### Changed
- **MCP settings are now scoped per project / per instance** — `EditorPrefs` is global to the machine, so settings were previously shared across every Unity project and instance (e.g. one project's manual port leaked to all others). `MCPSettingsManager` now namespaces keys into two tiers: **instance-scoped** (`Port`, `UseManualPort`, `AutoStart` — keyed by project path, unique per main Editor / ParrelSync clone / MPPM virtual player) and **project-scoped** (`StartOnVirtualPlayers`, project context, action-history and category settings — keyed by `PlayerSettings.productGUID`, shared by a project and its clones / virtual players). Existing settings are migrated to the new keys automatically on first load.

## [2.29.1] - 2026-05-21

### Fixed
- **MPPM Virtual Player detection on Unity 6** — `MCPScenarioCommands.IsVirtualPlayer()` (the gate behind the 2.29.0 "Start on Virtual Players" setting) only resolved the pre-Unity-6 type `Unity.Multiplayer.Playmode.CurrentPlayer`. On Unity 6 that API moved to `Unity.Multiplayer.PlayMode.CurrentPlayer` in the built-in `UnityEngine.MultiplayerModule`, so detection always returned false and the gate never engaged. It now resolves both locations (Unity 6 first, pre-6 fallback). Verified live on Unity 6000.5 with MPPM.

## [2.29.0] - 2026-05-21

### Added
- **"Start on Virtual Players" setting** — new MCP settings toggle controlling whether the bridge auto-starts on Multiplayer Play Mode (MPPM) virtual players. Previously every virtual player launched its own MCP bridge, which is usually unwanted noise. Default is **on** (behaviour unchanged); turn it off so only the main Editor runs a bridge. Virtual players are detected via `Unity.Multiplayer.Playmode.CurrentPlayer.IsMainEditor`; manual start on a virtual player still works. Addresses [unity-mcp-server#21](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/21).

## [2.28.1] - 2026-05-21

### Fixed
- **Manual (fixed) port not reclaimed after a domain reload** — with a manual port configured, `MCPBridgeServer.Start()` bound the port directly and gave up permanently on the first failure. Right after a domain reload the port can be briefly unbindable while the previous listener's socket is released; auto-port mode already survived this (it probes and falls back) but manual mode had neither probe nor retry. `Start()` now retries the same manual port up to 10 times on a 0.5s delay before giving up. Addresses [unity-mcp-server#10](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/10).

## [2.28.0] - 2026-05-21

### Added
- **Unity 6.5 (6000.5) compatibility** — The plugin compiles and runs on Unity 6.5. The InstanceID APIs deprecated as compile errors in 6.5 (`Object.GetInstanceID`, `EditorUtility.InstanceIDToObject`, `SerializedProperty.objectReferenceInstanceIDValue`, `AssetPreview.IsLoadingAssetPreview(int)`) are now routed through a version-gated `MCPObjectId` shim — it uses `EntityId` with `EntityId.ToULong`/`FromULong` on 6.5 and the classic APIs on 2021.3–6.4. Fixes [#14](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues/14) and [unity-mcp-server#24](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/24).

### Changed
- **`instanceId` is now a string** — Unity 6.5 entity ids are 64-bit values that exceed JavaScript's safe-integer range (2^53), so as JSON numbers they were rounded crossing the Node MCP server and object-by-`instanceId` resolution failed. The JSON `instanceId` field is now a decimal string on every Unity version (opaque, lossless). Requires `unity-mcp-server` ≥ 2.28.3.

## [2.27.2] - 2026-05-21

### Fixed
- **Roslyn assemblies not found on macOS** — `MCPEditorCommands.TryLoadRoslyn()` assumed the Windows/Linux `Data/` editor layout; on macOS the assemblies live inside `Unity.app/Contents/`, so `unity_execute_code` always failed with "Roslyn is not available". The lookup now detects the `.app` bundle and adds `Unity.app/Contents` as a data root, plus `Tools/ScriptUpdater`. Contributed by [@dougfy](https://github.com/dougfy) in [#13](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/13).

## [2.27.1] - 2026-05-21

### Fixed
- **UPM install compile failure (`CS0103` cascade)** — `MCPPrefsCommands`, `MCPConstraintCommands` and `MCPProfilerCommands` shipped `.cs.meta` files with hand-typed placeholder GUIDs. Under a UPM git install (`Library/PackageCache/`), Unity 6 silently skipped indexing those scripts, cascading into `CS0103` errors. The three GUIDs were regenerated with proper random values. Fixes [#11](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/issues/11). Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#12](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/12).

## [2.27.0] - 2026-04-22

### Fixed
- **Path-based lookup for inactive GameObjects** — `MCPGameObjectCommands.FindGameObject` now passes `FindObjectsInactive.Include` to `FindObjectsByType<GameObject>`. Every tool routed through path-based lookup (`prefab_info`, `set_active`, `info`, `delete`, `set_transform`, `reparent`, etc.) now works correctly on inactive GameObjects, whereas they previously failed with "GameObject not found". Fixes [unity-mcp-server#16](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/16). Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#8](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/8).
- **Prefab-instance detection on scene instances** — `MCPPrefabCommands.GetPrefabInfo` now uses `PrefabUtility.IsPartOfPrefabInstance` instead of `PrefabUtility.GetPrefabInstanceStatus == NotAPrefab`. This eliminates known false-negative cases (non-root children, instances with missing nested assets) where scene GameObjects that are valid prefab instances were reported as "not a prefab instance". Contributed by [@BadranRaza](https://github.com/BadranRaza) in [#8](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin/pull/8).
- **Bridge server started in AssetImportWorker subprocesses** — Unity spawns batch-mode `AssetImportWorker` subprocesses for parallel asset import, and these were running the plugin's `[InitializeOnLoad]` constructor and claiming ports in the 7890-7899 range on top of the main Editor. A single user with a few projects open could easily exhaust the range, blocking legitimate editor instances. `MCPBridgeServer` now early-returns when `Application.isBatchMode` is true.
- **Infinite retry loop on port exhaustion** — When no port was available, `MCPInstanceRegistry.FindAvailablePort()` returned `PortRangeStart` (7890) by default; `MCPBridgeServer.Start()` then retried via `EditorApplication.delayCall`, hit the same default, and looped forever, spamming `Failed to start on port 7890`. `FindAvailablePort()` now returns `-1` when nothing is free, and `Start()` gives up cleanly. Fixes [unity-mcp-server#10](https://github.com/AnkleBreaker-Studio/unity-mcp-server/issues/10).

### Changed
- **Declared minimum Unity version corrected** — `unityRelease` bumped from `0f1` to `18f1`. The plugin has been using `Object.FindObjectsByType` (introduced in Unity 2021.3.18) for several releases, so the declared minimum was inaccurate. No effective support window change.

## [2.26.0] - 2026-04-02

### Added
- **SpriteAtlas management** — 7 new HTTP endpoints for Unity SpriteAtlas workflow (contributed by [@zaferdace](https://github.com/zaferdace)):
  - `spriteatlas/create` — Create a new SpriteAtlas asset
  - `spriteatlas/info` — Get SpriteAtlas details (packed sprites, packing/texture settings)
  - `spriteatlas/add` — Add sprites or folders to a SpriteAtlas
  - `spriteatlas/remove` — Remove entries from a SpriteAtlas
  - `spriteatlas/settings` — Configure packing, texture, and platform-specific settings
  - `spriteatlas/delete` — Delete a SpriteAtlas asset
  - `spriteatlas/list` — List all SpriteAtlases in the project
- New `MCPSpriteAtlasCommands.cs` — Dedicated SpriteAtlas command handler
- **Self-test system overhaul** — Probes for all 43 command modules (18 new categories), robust test runner with domain reload resume and timeout handling

### Fixed
- **Unity 2023+ / Unity 6 compatibility** — Resolved 43 `CS0618` deprecation warnings across the codebase
- **Self-test conditional compilation** — UMA probe wrapped in `#if UMA_INSTALLED`, Scenario probe handles missing MPPM package gracefully

## [2.25.0] - 2026-03-25

### Added
- **UMA (Unity Multipurpose Avatar) integration** — 13 new HTTP endpoints for the complete UMA asset pipeline:
  - `uma/inspect-fbx` — Inspect FBX meshes for UMA compatibility
  - `uma/create-slot` — Create SlotDataAsset from mesh data
  - `uma/create-overlay` — Create OverlayDataAsset with texture assignments
  - `uma/create-wardrobe-recipe` — Create WardrobeRecipe combining slots and overlays
  - `uma/create-wardrobe-from-fbx` — Atomic FBX-to-wardrobe pipeline (inspect → slot → overlay → recipe in one call)
  - `uma/wardrobe-equip` — Equip/unequip wardrobe items on DynamicCharacterAvatar
  - `uma/list-global-library` — Browse the UMA Global Library contents
  - `uma/list-wardrobe-slots` — List available wardrobe slots
  - `uma/list-uma-materials` — List UMA-compatible materials
  - `uma/get-project-config` — Get UMA project configuration
  - `uma/verify-recipe` — Validate a WardrobeRecipe for missing references
  - `uma/rebuild-global-library` — Force rebuild the Global Library index
  - `uma/register-assets` — Register Slot/Overlay/Recipe assets in the Global Library
- New `MCPUMACommands.cs` — Dedicated UMA command handler with conditional compilation (`UMA_INSTALLED`)
- UMA routes wired into `MCPBridgeServer.cs`

## [2.24.0] - 2026-03-25

### Added
- **Unity Test Runner integration** — Run and manage tests directly from AI assistants
  - `testing/run-tests` — Start EditMode/PlayMode test runs, returns job ID for async polling
  - `testing/get-job` — Poll test job status and results (passed/failed/skipped counts, duration)
  - `testing/list-tests` — Discover available tests with names, categories, and run state
  - Async job-based pattern with deferred execution on Unity main thread
  - Supports filtering by test name, category, assembly, or group
- **Compilation error tracking via CompilationPipeline** — Dedicated error buffer independent of console log
  - `CompilationPipeline.assemblyCompilationFinished` captures errors/warnings per assembly
  - `CompilationPipeline.compilationStarted` auto-clears buffer on new compilation cycle
  - Thread-safe with lock-based synchronization
  - Not affected by console `Clear()` or Play Mode log flooding
  - Returns file, line, column, message, severity, assembly, and timestamp
  - Supports filtering by severity (`error`, `warning`, `all`) and count limit
  - Includes `isCompiling` flag in response
- **HTTP route `compilation/errors`** — New endpoint on the bridge server for the MCP server's `unity_get_compilation_errors` tool

### Fixed
- **Unity 2021.3 LTS compilation compatibility** — Replaced `string.Contains(string, StringComparison)` with `IndexOf` for .NET Standard 2.0 compatibility
- **Operator precedence bug** — Fixed `!IndexOf >= 0` (CS0023) to `IndexOf < 0` in test name filtering

## [2.9.1] - 2026-02-26

### Changed
- **MCP connector renamed to `unity-mcp`** for better Cowork discovery (technical name only)
  - AnkleBreaker branding preserved in all user-facing UI (menu, dashboard, logs, tooltips)
  - Menu item remains: `Window > AB Unity MCP`
  - Log prefix remains: `[AB-UMCP]`
- Updated README with clear two-part installation instructions and Cowork setup guide
- Added Project Context to dashboard documentation

## [2.9.0] - 2026-02-26

### Added
- Project Context System — auto-inject project documentation to AI agents
- MCPContextManager for file discovery and template generation
- Context endpoints on HTTP bridge (direct read-only, bypasses queue)
- Context UI foldout in dashboard window

## [2.8.0] - 2026-02-25

### Added
- Multi-agent async request queue with fair round-robin scheduling
- Agent session tracking and action logging
- Read batching (up to 5/frame) and write serialization (1/frame)
- Queue management API endpoints
- Dashboard with live queue monitoring and agent sessions
- Self-test system for verifying all 21 categories
- Toolbar status element with server controls

## [1.0.0] - 2026-02-25

### Added
- Initial release
- HTTP bridge server on localhost:7890
- Scene management (open, save, create, hierarchy)
- GameObject operations (create, delete, inspect, transform)
- Component management (add, remove, get/set properties)
- Asset management (list, import, delete, prefabs, materials)
- Script operations (create, read, update)
- Build system (multi-platform builds)
- Console log access
- Play mode control
- Editor state monitoring
- Project info retrieval
- Menu item execution
- MiniJson serializer (zero dependencies)
