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
| Editor stability | `unity_wait_editor_idle` | `wait/editor-idle` | Wait for compilation, package refresh, domain reload, and asset import to settle before issuing the next command. |
| Prefab asset editing | `unity_prefab_asset_instantiate_prefab` | `prefab-asset/instantiate-prefab` | Instantiate one prefab asset inside another prefab asset under a selected child path. |
| Prefab asset editing | `unity_prefab_asset_move_gameobject` | `prefab-asset/move-gameobject` | Move or reorder a GameObject inside a prefab asset without opening Prefab Mode manually. |
| Prefab asset search | `unity_prefab_asset_find` | `prefab-asset/find` | Find prefab children by name/path, component type, and serialized property value. |
| Safe assets | `unity_asset_rename` | `asset/rename` | Rename an asset through `AssetDatabase.RenameAsset`, preserving `.meta`, GUID, and references. |
| Safe assets | `unity_asset_move` | `asset/move` | Move an asset through `AssetDatabase.MoveAsset`, preserving `.meta`, GUID, and references. |
| Console inspection | `unity_console_query` | `console/query` | Filter recent console entries by time, log type, message, source stack frame, full stack text, or only entries after the last Play transition. |
| Animator editing | `unity_animation_transition_info` | `animation/transition-info` | Read full Animator transition details, including conditions, exit time, duration, offset, and interruption settings. |
| Animator editing | `unity_animation_update_state` | `animation/update-state` | Modify an existing Animator state, including motion, speed, tag, position, write defaults, and default state. |
| Animator editing | `unity_animation_update_transition` | `animation/update-transition` | Modify an existing transition and add, update, remove, or replace transition conditions. |
| Animator editing | `unity_animation_connect_states` | `animation/connect-states` | Create directed pairwise transitions between a list of Animator states. |
| Package management | `unity_packages_update_git` | `packages/update-git` | Update a Git package through a deferred route so Unity does not block its main thread while Package Manager resolves the dependency. |

## Notes

- Use the upstream README for the general feature list and MCP setup flow.
- This fork intentionally keeps the package smaller by removing local documentation images.
- The package is still the Unity Editor side only. You need an MCP server/client setup to call the tools from an assistant.
- Package updates can take time if Unity needs to fetch from GitHub. Pinning commits keeps project state reproducible.

## License

This fork follows the upstream license. See [LICENSE](LICENSE).
