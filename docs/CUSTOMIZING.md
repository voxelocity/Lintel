# Customizing Lintel — themes & widgets

Lintel reads your custom **themes** and **widgets** from JSON files, so you can build your own
without touching code or recompiling. Everything lives in:

```
%AppData%\Lintel\themes\     ← your *.json themes
%AppData%\Lintel\widgets\    ← your *.json widgets
```

> Files may contain `//` comments and trailing commas, so the templates below stay readable.
> A bad/malformed file is skipped (it won't crash the bar).

**Templates to start from:** [`docs/templates/`](templates)
- [`theme.template.json`](templates/theme.template.json)
- [`widget-command.template.json`](templates/widget-command.template.json)
- [`widget-launcher.template.json`](templates/widget-launcher.template.json)

## How to add one

Three ways — all equivalent:
1. **Import button** — the bar's **Add-widget menu** (＋ in the Customize pill) has **Upload widget** / **Upload theme**; **Settings → Customization** has the same plus **Open folder**.
2. **Drop a file** into the `themes` / `widgets` folder and restart Lintel.
3. Edit a file already in the folder.

Imported themes are applied instantly; imported widgets are dropped onto the bar right away.

## Themes

A theme is colours + a few toggles. All colours are `#AARRGGBB`.

| Field | Meaning |
|---|---|
| `name` | Shown in the theme picker (make it unique) |
| `bubbleIdle` / `bubbleHover` | Per-widget background, normal / hovered |
| `cornerRadius` | Widget roundness (0 = square) |
| `padding` | Horizontal padding inside a widget |
| `spacing` | Gap between widgets |
| `iconSaturation` | `1` = full colour, `0` = grayscale |
| `acrylic` / `acrylicTint` | Translucent bar + tint over it (lower the tint alpha for clearer glass) |
| `frostedGlass` | **Real frosted glass** — blurs the desktop wallpaper behind the bar |
| `aeroBlur` | (legacy OS blur; ignored on Win11 builds where it no longer works) |
| `bottomHighlight` | Hairline along the bottom edge |
| `widgetDividers` | Divider line between every widget |
| `separatedZones` / `zoneBackground` | Left/center/right become floating pills |
| `barTop` / `barBottom` | Vertical bar gradient (e.g. the Windows XP Luna bar) |
| `dropdownColor` | Force the dropdown panel colour |
| `barHeight` | Override the bar height in px |
| `fontFamily` | Theme font, e.g. `Tahoma` |
| `glossStrength` | `0–1` glossy reflection across the top half (Aero/Luna shine) |
| `topEdge` / `bottomEdge` | Bright/dark hairlines on the top/bottom edges |
| `bubbleBorder` / `bubbleBorderThickness` | Raised-button outline on each widget |
| `bubbleGloss` | `0–1` glossy sheen on each widget bubble |

> These cosmetic fields are what make the built-in **Windows XP / Vista / 7** themes look distinct — a custom theme can use the exact same knobs (size, font, gloss, gradient, bevels) to be just as unique.

## Widgets

Two easy types, no coding required.

### `command` — run something, show its output
Runs `cmd /c <command>` every `intervalMs` and shows the **first line** of output.

| Field | Meaning |
|---|---|
| `key` | Unique id (used to save it in your layout) |
| `name` | Shown in the Add menu + tooltip |
| `type` | `"command"` |
| `command` | The shell command to run |
| `intervalMs` | How often to re-run (min 500) |
| `icon` | Emoji, single letter, a built-in key, or SVG path data |
| `accent` | Icon colour `#AARRGGBB` |
| `onClick` | Optional URL/file/app to open on click |

> Command widgets only run while **Allow command widgets** is enabled (Advanced settings, on by default). They run with your user privileges — only use commands you trust.

### `launcher` — a clickable shortcut
Static icon + label that opens `onClick` (a URL, file, or app). Same fields as above minus `command`/`intervalMs`.

### Icons
`icon` accepts any of:
- a **built-in key**: `cpu`, `ram`, `gpu`, `disk`, `net`, `battery`, `clock`, `note`, `windows`, `workspaces`, `settings`, `media`, `claude`, `github`
- a **single emoji or character**: `🌡`, `🚀`, `A`
- raw **SVG-style path data**: `"M12,2 L22,20 L2,20 Z"` (24×24 coordinate space)
- `""` for no icon
