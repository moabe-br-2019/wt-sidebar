# WT Sidebar

Vertical tab sidebar for Windows Terminal. It is a separate app that sticks to the left edge of the terminal window, so the official Windows Terminal keeps working and updating unchanged.

## Features

- Lists every tab with its full title, profile icon and tab color; the active tab is highlighted.
- Shows Claude Code session state from the tab title: green `✳` = waiting for you, orange half-circle = working.
- Click to switch tabs, hover `×` or middle click to close, `+ Nova aba` to open a tab, `⌄` for the profile menu (built from `settings.json`).
- The `⌄` menu also has **Claude Code em** and **Codex em**: open a new tab in a recent folder (or pick one) running `claude` or `codex`. The last 10 folders are kept in `%LOCALAPPDATA%\WtSidebar\recent-folders.txt`.
- Right click a tab for the same actions as the terminal's own tab menu (color, rename, duplicate, split, move, export, find, close).
- Follows the terminal when it moves, resizes or minimizes. A maximized terminal becomes "work area minus the sidebar"; maximizing again restores the previous size.
- `Ctrl+Shift+B` (while the terminal is focused) or the `«` button collapses it to a narrow icon column.

## How it works

- Tabs are read through UI Automation (`TabItem` elements of the `CASCADIA_HOSTING_WINDOW_CLASS` window), polled every 500 ms.
- Switching and closing use the tab's `SelectionItemPattern` and its `CloseButton`.
- Tab color and profile icon are sampled from the screen while the terminal is in the foreground, because UI Automation does not expose them.
- The sidebar is an owned, non-activating tool window positioned with `SetWinEventHook` location events.
- Tab menu actions select the tab and send the action's keyboard shortcut. Actions without a default shortcut need these bindings in the terminal's `settings.json`:

| Action | Keys |
|---|---|
| `openTabColorPicker` | `ctrl+alt+shift+c` |
| `openTabRenamer` | `ctrl+alt+shift+r` |
| `exportBuffer` | `ctrl+alt+shift+e` |
| `moveTab` (`window: new`) | `ctrl+alt+shift+n` |
| `moveTab` (`direction: backward` / `forward`) | `ctrl+alt+shift+left` / `right` |
| `closeOtherTabs` | `ctrl+alt+shift+o` |
| `closeTabsAfter` | `ctrl+alt+shift+w` |
| `splitPane` (`splitMode: duplicate`) | `alt+shift+d` |

## Build

No SDK needed; it compiles with the C# compiler that ships with Windows (.NET Framework 4):

```powershell
.\build.ps1
.\WtSidebar.exe
```

To start with Windows, put a shortcut to `WtSidebar.exe` in `shell:startup`. Click **WT Sidebar ⌄** in the header and choose **Fechar WT Sidebar** to quit.

## Limitations

- Supports a single Windows Terminal window.
- Tab colors and icons are only read while the terminal is in front and the tab is visible in the top tab strip.
- While a terminal flyout menu is open, UI Automation hides the tabs; the sidebar keeps the last known list.
