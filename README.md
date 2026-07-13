# Unity MCP Plugin Fork

This is a lightweight fork of [AnkleBreaker Studio's Unity MCP Plugin](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin).

The upstream project provides the full Unity Editor MCP bridge, broad tool coverage, dashboard, optional package integrations, FAQ, changelog, and general documentation. This fork keeps that base and adds a small set of workflow tools that are useful for automated Unity project editing.

For complete upstream documentation, install notes, supported tool categories, screenshots, and general usage, see:

- [Original Unity MCP Plugin](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin)
- [Unity MCP Server](https://github.com/AnkleBreaker-Studio/unity-mcp-server)

## Install

In Unity Package Manager, choose **Add package from git URL...** and enter:

```text
https://github.com/VM233/unity-mcp-plugin.git
```

For a reproducible project dependency, pin a specific commit:

```text
https://github.com/VM233/unity-mcp-plugin.git#<commit-hash>
```

The bridge runs inside the Unity Editor on `127.0.0.1`, usually port `7890`. Verify it with:

```text
http://127.0.0.1:7890/api/ping
```

## Fork Additions

| Area | MCP tool name | HTTP route | Purpose |
|------|---------------|------------|---------|
| Fallback entrypoint | `unity_advanced_execute` | `advanced/execute` | Fallback route for commands that do not have a concrete tool yet. Prefer route-specific `unity_*` tools first. |
| MCP diagnostics | `unity_mcp_health` | `mcp/health` | Inspect bridge state, queue state, sessions, process memory, recent actions, and slow requests. |
| MCP diagnostics | `unity_mcp_set_autostart` | `mcp/set-autostart` | Enable or disable bridge auto-start for the current Unity Editor instance. |
| Editor stability | `unity_wait_editor_idle` | `wait/editor-idle` | Wait for compilation, package refresh, domain reload, and asset import to settle before issuing the next command. |
| Testing | `unity_testing_list_tests` | `testing/list-tests` | List discoverable EditMode or PlayMode tests with filters. |
| Testing | `unity_testing_run_tests` | `testing/run-tests` | Start a Unity Test Runner job. |
| Testing | `unity_testing_get_job` | `testing/get-job` | Poll test progress and detailed results. |
| Package testing | `unity_testing_run_package_tests` | `testing/run-package-tests` | Temporarily enable Git package tests, run them across domain reloads, and restore the package manifest exactly. |
| Package testing | `unity_testing_get_package_job` | `testing/get-package-job` | Poll the persistent package test workflow and its final test result. |
| Localization | `unity_localization_upsert_entry` | `localization/upsert-entry` | Prevalidate and upsert one or more String, Smart String, or Asset Table entries with `execution.mode` controlling immediate or frame-batched execution. |
| Multi-editor safety | `unity_instance_current` | `instance/current` | Return the current Editor MCP instance identity, including project path and port. |
| Multi-editor safety | `unity_instance_list` | `instance/list` | List registered Editor MCP instances across open Unity projects. |
| Multi-editor safety | `unity_instance_resolve` | `instance/resolve` | Resolve exactly one Editor MCP instance by project path, project name, or port. |
| Multi-editor safety | `unity_instance_assert_project` | `instance/assert-project` | Verify that a request reached the expected Unity project. |
| Prefab asset editing | `unity_prefab_asset_add_component` | `prefab-asset/add-component` | Add a component after waiting for a newly compiled script type to become available; returns prefab YAML diff by default. |
| Prefab asset editing | `unity_prefab_asset_add_gameobject` | `prefab-asset/add-gameobject` | Create a child GameObject inside a prefab asset. |
| Prefab asset editing | `unity_prefab_asset_transaction_edit` | `prefab-asset/transaction-edit` | Apply ordered prefab edits in one load/save transaction with `execution.mode` controlling immediate or frame-batched execution. |
| Prefab asset editing | `unity_prefab_asset_instantiate_prefab` | `prefab-asset/instantiate-prefab` | Instantiate one prefab asset inside another prefab asset under a selected child path. |
| Prefab asset editing | `unity_prefab_asset_instantiate_child_prefab` | `prefab-asset/instantiate-child-prefab` | Clearer alias for `prefab-asset/instantiate-prefab`; use this when editing a prefab asset, not the scene. |
| Prefab asset editing | `unity_prefab_asset_move_gameobject` | `prefab-asset/move-gameobject` | Move or reorder a GameObject inside a prefab asset without opening Prefab Mode manually. |
| Prefab asset editing | `unity_prefab_asset_move_component` | `prefab-asset/move-component` | Atomically move a component between GameObjects while preserving its serialized data and remapping references to the moved component. |
| Prefab asset editing | `unity_prefab_asset_remove_component` | `prefab-asset/remove-component` | Remove a component from a GameObject inside a prefab asset. |
| Prefab asset editing | `unity_prefab_asset_remove_gameobject` | `prefab-asset/remove-gameobject` | Remove a child GameObject from inside a prefab asset. |
| Prefab asset editing | `unity_prefab_asset_set_property` | `prefab-asset/set-property` | Set a serialized property on a component inside a prefab asset. |
| Prefab asset editing | `unity_prefab_asset_set_reference` | `prefab-asset/set-reference` | Set an ObjectReference property on a component inside a prefab asset. |
| Prefab asset search | `unity_prefab_asset_find` | `prefab-asset/find` | Find prefab children by name/path, component type, and serialized property value. |
| Prefab asset search | `unity_prefab_asset_hierarchy` | `prefab-asset/hierarchy` | Read a prefab asset hierarchy directly from disk. |
| Prefab asset search | `unity_prefab_asset_get_properties` | `prefab-asset/get-properties` | Read serialized component properties inside a prefab asset. |
| Scene editing | `unity_scene_instantiate_prefab` | `scene/instantiate-prefab` | Instantiate a prefab asset into the currently open scene. |
| Safe assets | `unity_asset_refresh` | `asset/refresh` | Refresh AssetDatabase, optionally forcing update or importing specific asset paths before prefab/package operations. |
| Safe assets | `unity_asset_import` | `asset/import` | Copy an external image or other asset into Assets and configure TextureImporter/Sprite settings in the same operation. |
| Safe assets | `unity_asset_rename` | `asset/rename` | Rename an asset through `AssetDatabase.RenameAsset`, preserving `.meta`, GUID, and references. |
| Safe assets | `unity_asset_move` | `asset/move` | Preflight and move one or more assets, preserving `.meta` GUIDs and rolling back completed moves when configured to stop on failure. |
| Scene references | `unity_component_set_reference` | `component/set-reference` | Assign one or more ObjectReference properties with `execution.mode` and shared target defaults. |
| Serialization | `unity_serialized_object_get` | `serialized-object/get` | Read serialized properties from a scene object, component, or asset. |
| Serialization | `unity_serialized_object_set` | `serialized-object/set` | Set one serialized property on a scene object, component, or asset. |
| Console inspection | `unity_console_query` | `console/query` | Filter recent console entries by time, log type, message, source stack frame, full stack text, or only entries after the last Play transition. |
| Compilation inspection | `unity_compilation_errors` | `compilation/errors` | Return tracked Unity compilation errors. |
| Animator editing | `unity_animation_transition_info` | `animation/transition-info` | Read full Animator transition details, including conditions, exit time, duration, offset, and interruption settings. |
| Animator editing | `unity_animation_update_state` | `animation/update-state` | Modify an existing Animator state, including motion, speed, tag, position, write defaults, and default state. |
| Animator editing | `unity_animation_update_transition` | `animation/update-transition` | Modify an existing transition and add, update, remove, or replace transition conditions. |
| Animator editing | `unity_animation_connect_states` | `animation/connect-states` | Create directed pairwise transitions between a list of Animator states. |
| UI Toolkit assets | `unity_uitoolkit_asset_inspect` | `uitoolkit/asset-inspect` | Inspect UXML/USS assets for VisualElement names, type matches, USS classes, and default size declarations. |
| UI Toolkit runtime | `unity_uitoolkit_runtime_documents` | `uitoolkit/runtime-documents` | List runtime UIDocuments and their root visual element metadata. |
| UI Toolkit runtime | `unity_uitoolkit_runtime_tree` | `uitoolkit/runtime-tree` | Read a UIDocument visual tree, including optional style and bounds data. |
| UI Toolkit runtime | `unity_uitoolkit_runtime_query` | `uitoolkit/runtime-query` | Query runtime VisualElements by tree path, VisualElementPath name list, name, class, type, or text. |
| UI Toolkit runtime | `unity_uitoolkit_runtime_style` | `uitoolkit/runtime-style` | Read inline style, resolved style, bounds, and background asset metadata for a runtime element. |
| UI Toolkit runtime | `unity_uitoolkit_runtime_repaint` | `uitoolkit/runtime-repaint` | Repaint a runtime UIDocument or one selected VisualElement. |
| UI Toolkit runtime | `unity_uitoolkit_refresh` | `uitoolkit/refresh` | Refresh UI Toolkit assets and repaint runtime and editor panels. |
| UI Toolkit runtime | `unity_uitoolkit_wait_refresh` | `uitoolkit/wait-refresh` | Refresh UI Toolkit assets, repaint panels, and wait for stable editor frames. |
| UI Toolkit runtime | `unity_uitoolkit_assert_layout` | `uitoolkit/assert-layout` | Assert runtime layout constraints such as no-gap/no-overlap edge touching, edge alignment, center alignment, containment, and expected size. |
| UI Toolkit visual QA | `unity_uitoolkit_locate_element` | `uitoolkit/locate-element` | Locate an Editor or runtime UI Toolkit element and return bounds, crop rect, and context for later screenshots or pixel checks. |
| UI Toolkit visual QA | `unity_uitoolkit_capture_element` | `uitoolkit/capture-element` | Capture a UI Toolkit element by locating it in an Editor window or runtime UIDocument and cropping its containing window screenshot. |
| UI Toolkit visual QA | `unity_uitoolkit_compare_element` | `uitoolkit/compare-element` | Capture a UI Toolkit element and compare the crop with a reference image, optionally writing a diff image. |
| UI Toolkit visual QA | `unity_uitoolkit_generated_children` | `uitoolkit/generated-children` | Inspect generated UI Toolkit children such as arrows, checkmarks, scrollers, TabView internals, and unnamed `unity-*` subparts. |
| UI Toolkit visual QA | `unity_uitoolkit_resource_audit` | `uitoolkit/resource-audit` | Audit target elements and descendants for resolved background assets, highlighted-state misuse, and missing or forbidden assets. |
| UI Builder | `unity_uitoolkit_builder_preview` | `uitoolkit/builder-preview` | Open a UXML asset in UI Builder, wait for the preview to settle, and optionally capture the UI Builder window. |
| Screenshot utilities | `unity_screenshot_crop` | `screenshot/crop` | Crop a screenshot or image file to a PNG for focused visual inspection. |
| Graphics utilities | `unity_graphics_image_alpha_bounds` | `graphics/image-alpha-bounds` | Inspect a PNG or texture asset and return visible alpha pixel bounds plus transparent margins. |
| Graphics utilities | `unity_graphics_rect_gap` | `graphics/rect-gap` | Measure a gap or overlap between two rectangles along selected edges. |
| Graphics utilities | `unity_graphics_annotate_rects` | `graphics/annotate-rects` | Draw rectangle borders onto screenshots or images for visual verification reports. |
| Graphics utilities | `unity_graphics_compare_images` | `graphics/compare-images` | Compare two screenshots or crop regions, return difference bounds and samples, and optionally write a highlighted diff image. |
| Sprite pipeline | `unity_sprite_sheet_info` | `sprite/sheet-info` | Inspect a sliced sprite sheet and return texture and sprite metadata. |
| Sprite pipeline | `unity_sprite_replace_and_slice` | `sprite/replace-and-slice` | Replace a sprite sheet PNG and slice it into numbered sprites while preserving sprite IDs by name. |
| Sprite pipeline | `unity_sprite_slice_sheet` | `sprite/slice-sheet` | Slice an existing sprite sheet into numbered sprites. |
| Sprite pipeline | `unity_sprite_update_animation_clip` | `sprite/update-animation-clip` | Rebuild a SpriteRenderer sprite animation curve from a sheet's sprites. |
| Sprite pipeline | `unity_sprite_replace_slice_update_clip` | `sprite/replace-slice-update-clip` | Replace a sheet, slice it, and update an AnimationClip in one call. |
| Texture pipeline | `unity_texture_apply_sprite_preset` | `texture/apply-sprite-preset` | Apply high-level TextureImporter/Sprite settings, including pixel sprite preset, PPU, pivot, border, and reference settings. |
| Texture pipeline | `unity_texture_info` | `texture/info` | Inspect texture dimensions and TextureImporter settings, including sprite PPU, pivot, and border. |
| Texture pipeline | `unity_texture_import_image` | `texture/import-image` | Import an image from a URL or local file into Assets, dedupe by hash, and apply sprite import settings. |
| Texture pipeline | `unity_texture_check_ui_import_settings` | `texture/check-ui-import-settings` | Check UI pixel-art image import settings, including pixel sprite defaults plus optional expected dimensions, border, and max texture size. |
| Build testing | `unity_build_run_test` | `build/run-test` | Overwrite/build a player, launch it, sample Player.log, optionally capture its window, and terminate it. |
| Package management | `unity_packages_update_git` | `packages/update-git` | Update a Git package through a deferred route; same-commit updates skip Unity Package Manager resolve by default. |
| Project extensions | `unity_project_tools_list` | `project-tools/list` | List project-defined extension tools from loaded Unity editor assemblies. |
| Project extensions | `unity_project_tools_execute` | `project-tools/execute` | Execute a project-defined extension tool by `toolName`. |

## Project Extensions

Project-specific batch tools can live in the Unity project instead of this package. Put an Editor script in the project and mark a static method with `MCPProjectToolAttribute`:

```csharp
using System.Collections.Generic;
using UnityMCP.Editor;

public static class ProjectMcpTools
{
    [MCPProjectTool("battleidle/add-property",
        Description = "Create and register a BattleIdle property.",
        InputSchemaJson = "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"}},\"required\":[\"id\"]}")]
    public static object AddProperty(Dictionary<string, object> args)
    {
        // Project-specific AssetDatabase / prefab / settings edits go here.
        return new { success = true };
    }
}
```

Project tools are exposed in metadata as first-class concrete routes and tool names, using their declared schema:

```json
{
  "route": "project-tools/call/battleidle/add-property",
  "toolName": "unity_pt_battle_add_prop",
  "inputSchema": {
    "type": "object",
    "properties": {
      "id": {
        "type": "string"
      }
    },
    "required": ["id"]
  }
}
```

Older clients can still call `project-tools/list` to discover tools and `project-tools/execute` with:

```json
{
  "toolName": "battleidle/add-property",
  "args": {
    "id": "gold_amount"
  }
}
```

If an MCP client has stale tool metadata, use the concrete direct route `project-tools/call/battleidle/add-property` first. Use `advanced/execute` only as a fallback for clients that cannot call newly exposed routes yet.

## Notes

- Use the upstream README for the general feature list and MCP setup flow.
- `_meta/tools` defaults to compact first-class metadata, 50 tools per page, and no schemas. Use `includeSchema=true`, `offset`, `limit`, and optional `category` as needed. Legacy duplicate collections are returned only with `compact=false&includeCollections=true`.
- Error payloads are normalized with `success=false`, `errorCode`, `message`, and `retryable`. Successful payloads keep their existing shape for compatibility.
- Queue tickets now keep small status snapshots through Unity domain reloads. If a ticket cannot resume after reload, polling returns `ticket_lost_after_reload` with `retryable=true` instead of an ambiguous expired-ticket response.
- For multiple Unity projects open at once, call `instance/resolve` with the target `projectPath`, then send later calls to the returned `port`. Also pass `expectedProjectPath` on mutating calls; the Editor rejects the request with `wrong_unity_project` if it reaches the wrong project.
- `unity_wait_editor_idle` waits for both consecutive idle editor frames and a continuous idle time window (`stableMs`, default `500`) to avoid returning before a delayed compile or asset import starts.
- `mcp/health` is intended for diagnosing editor slowdown caused by MCP usage. It does not stop the bridge; use `mcp/set-autostart` to prevent the bridge from coming back automatically after reload.
- `texture/import-image` is the preferred route for Figma/exported UI images: pass `sourceUrl` or `sourcePath`, `targetPath` or `targetFolder` + `assetName`, then use `preset=pixel-sprite` and `border` when needed.
- For UI visual QA, use `uitoolkit/locate-element` first to confirm the measured semantic target, then `uitoolkit/capture-element` or `uitoolkit/compare-element` for local crops. Use `uitoolkit/generated-children` when controls such as `TabView`, `DropdownField`, `Scroller`, or `ToggleButtonGroup` may be drawing default child indicators.
- Prefab asset mutation tools return a summary-only `prefabFileDiff` by default. Pass `includePrefabFileDiff=false` to suppress it or request `prefabFileDiffMode=minimal/full` when line details are required. Use `unity_prefab_asset_transaction_edit` for multi-step edits; the legacy batch alias remains available through the advanced catalog.
- `uitoolkit/builder-preview` opens and screenshots UI Builder. Unity does not expose a stable public UI Builder zoom API, so the route records requested zoom values but does not use reflection to force the viewport zoom.
- `build/run-test` is a high-level test loop for local player builds. Keep `overwrite=true` for repeat tests so output folders do not accumulate.
- `unity_packages_update_git` defaults to `skipIfResolved=true`. When the requested ref is a commit hash already recorded in `packages-lock.json`, the tool returns `skipped=true` without asking Unity Package Manager to resolve again. Pass `force=true` to force a resolve.
- This fork intentionally keeps the package smaller by removing local documentation images.
- The package is still the Unity Editor side only. You need an MCP server/client setup to call the tools from an assistant.
- Package updates can take time if Unity needs to fetch from GitHub. Pinning commits keeps project state reproducible.

## License

This fork follows the upstream license. See [LICENSE](LICENSE).
