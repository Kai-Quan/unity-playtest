# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] — 2026-08-17

First public release.

### Added

- **`play`** — drives the game through the real Input System as synthesized device
  events, so an agent's click travels the same path a player's does. Returns two
  pictures per action: a numbered contact sheet of the whole gesture, and the final
  screen at full size.
- **`inspect`** — the developer's camera. `look`, `turn`, `pan`, `zoom`, `frame`,
  `orbit`, `plan`. Answers staging questions a play screenshot cannot: does this
  float, do these intersect, is it the size I think it is. Works during play mode,
  where it shows the live scene.
- **`snapshot`** — the scene as numbers rather than pictures. `write` records a
  subtree's transforms and mesh assignments to JSON; `diff` reports only what has
  moved, been added or been deleted since. Refuses during play mode, where every
  transform is runtime state.
- `PlaytestBridge.AddProbe` for publishing game state without the package knowing
  anything about your game.
- **Tools → Playtest → Print MCP config**, which resolves the installed package
  path so nobody has to hunt through `Library/PackageCache`.

### Notes

- Stripped from release builds by `#if UNITY_EDITOR || DEVELOPMENT_BUILD`.
- Input is forced to `IgnoreFocus`, so it keeps working while the editor is in the
  background. Without it the harness looks alive — screenshots keep arriving —
  while every keypress is silently dropped.

### Known issues

- **Pointer position is not injected.** `Mouse.current.position` reads `(0,0)`
  regardless of the coordinate sent, and a Mouse/Keyboard device pair leaks on
  every action, so `Mouse.current` is not the device the click reaches. Keys and
  key-driven actions work; anything that needs a cursor position — hover, uGUI
  buttons, world-space raycast clicks — does not. Fix in progress.
