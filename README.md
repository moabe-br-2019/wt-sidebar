# WT Sidebar

Vertical tab sidebar for Windows Terminal. It is a separate app that sticks to the left edge of the terminal window, so the official Windows Terminal keeps working and updating unchanged.

## Features

- Lists every tab with its full title, profile icon and tab color; the active tab is highlighted.
- Shows Claude Code session state from the tab title: green `✳` = waiting for you, orange half-circle = working.
- Click to switch tabs, hover `×` or middle click to close, `+ New tab` to open a tab, `⌄` for the profile menu (built from the terminal's `settings.json`).
- The `⌄` menu also lists agent commands (**Claude Code in**, **Codex in**, and any you add in Settings): each opens a new tab in a recent folder (or one you pick) running that command. The last 10 folders are kept in `%LOCALAPPDATA%\WtSidebar\recent-folders.txt`.
- Right click a tab for the same actions as the terminal's own tab menu (color, rename, duplicate, split, move, export, find, close).
- Follows the terminal when it moves, resizes or minimizes. A maximized terminal becomes "work area minus the sidebar"; maximizing again restores the previous size.
- `Ctrl+Shift+B` (while the terminal is focused) or the `«` button collapses it to a narrow icon column.
- English or Portuguese interface, following the Windows language by default.

## Settings

**WT Sidebar ⌄ → Settings…** in the header:

- Language: automatic (Windows language), Portuguese or English.
- Width (160 to 400 px) and whether it starts collapsed.
- Start with Windows (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
- Check for updates automatically. When off, the app only goes online when you click **Check for updates**.
- Agent commands: name and command of each entry in the `⌄` menu (for example `Gemini` / `gemini`). The command runs in PowerShell, in a new tab, in the chosen folder.
- **Set up terminal shortcuts**: adds the terminal shortcuts the tab menu needs again (see below).

They are saved in `%LOCALAPPDATA%\WtSidebar\settings.json`.

## How it works

- Tabs are read through UI Automation (`TabItem` elements of the `CASCADIA_HOSTING_WINDOW_CLASS` window), polled every 500 ms.
- Switching and closing use the tab's `SelectionItemPattern` and its `CloseButton`.
- Tab color and profile icon are sampled from the screen while the terminal is in the foreground, because UI Automation does not expose them.
- The sidebar is an owned, non-activating tool window positioned with `SetWinEventHook` location events.
- Tab menu actions select the tab and send the action's keyboard shortcut. Actions without a default shortcut need these bindings in the terminal's `settings.json`. The installer adds the missing ones as `WtSidebar.*` actions (with a backup at `settings.json.wtsidebar.bak`), skips keys already used by other actions, and `-Uninstall` removes them:

| Action | Keys |
|---|---|
| `openTabColorPicker` | `ctrl+alt+shift+c` |
| `openTabRenamer` | `ctrl+alt+shift+r` |
| `exportBuffer` | `ctrl+alt+shift+e` |
| `moveTab` (`window: new`) | `ctrl+alt+shift+n` |
| `moveTab` (`direction: backward` / `forward`) | `ctrl+alt+shift+left` / `right` |
| `closeOtherTabs` | `ctrl+alt+shift+o` |
| `closeTabsAfter` | `ctrl+alt+shift+w` |

## Install

Paste in PowerShell:

```powershell
irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1 | iex
```

No SDK and no git needed: the installer downloads the source of the latest release, compiles it with the C# compiler that ships with Windows (.NET Framework 4), copies it to `%LOCALAPPDATA%\Programs\WtSidebar`, adds a Start menu shortcut, sets it to start with Windows, adds the terminal shortcuts the tab menu needs and starts it. Messages follow the Windows language (`-Lang en` or `-Lang pt` to choose).

With options, run it as a script block:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/moabe-br-2019/wt-sidebar/main/install.ps1))) -NoStartup
```

- `-NoStartup` does not start it with Windows.
- `-NoKeys` leaves the terminal's `settings.json` untouched (see the shortcut table in How it works).
- `-Ref v1.2.0` installs that tag (or branch) instead of the latest release.
- `-Uninstall` removes the app, its shortcuts and its terminal shortcuts (settings and recent folders stay in `%LOCALAPPDATA%\WtSidebar`).

Click **WT Sidebar ⌄** in the header and choose **Close WT Sidebar** to quit.

## Updates

The sidebar checks the latest GitHub release on start and every 6 hours. When there is a newer one, the header menu shows **Update to vX.Y.Z**; **Check for updates** checks on demand (and is the only check when automatic checks are off in Settings). Updating closes the sidebar, runs the installer again for the new tag and starts it. The log goes to `%LOCALAPPDATA%\WtSidebar\update.log`.

## Development

```powershell
git clone https://github.com/moabe-br-2019/wt-sidebar.git
cd wt-sidebar
.\build.ps1          # compiles WtSidebar.exe in the clone, no auto update
.\install.ps1        # installs the local code (version from git describe)
```

To publish a version, tag it and create a GitHub release; installed copies pick it up on their next check:

```powershell
git tag v1.2.0; git push origin v1.2.0
gh release create v1.2.0 --generate-notes
```

## Limitations

- Supports a single Windows Terminal window.
- Tab colors and icons are only read while the terminal is in front and the tab is visible in the top tab strip.
- While a terminal flyout menu is open, UI Automation hides the tabs; the sidebar keeps the last known list.

## License

[MIT](LICENSE)
