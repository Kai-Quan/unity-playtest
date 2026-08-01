# Playtest

Lets an AI agent play your Unity game the way a person does, and see what happened.

It presses keys and moves a mouse through the **real Input System** as synthesized
device events, so every action travels the same path a player's does — bindings,
action maps, and whatever gates the game puts in front of them. Nothing here can
click a button the player cannot reach, which is the entire point: a test that can
reach further than a player is worse than no test.

## Install

Add to your project's `Packages/manifest.json`:

```json
"com.elegist.playtest": "https://github.com/<you>/<repo>.git?path=/Packages/com.elegist.playtest"
```

or drop the folder into `Packages/` to embed it.

Then in Unity: **Tools → Playtest → Print MCP config**. It prints the block to paste
into `.mcp.json`, with the path already resolved — an installed package lives under
`Library/PackageCache` with a hash in its name, so nobody should have to find it.

Needs Node (no npm install — the server is dependency-free) and
`com.unity.inputsystem`.

## Use

One action in, two pictures out.

```
{"action":"start"}                                run the game, answer once it is up
{"action":"click","x":480,"y":270}                click where you saw something
{"action":"key","key":"w","seconds":3}            hold a key, sampled as it goes
{"action":"drag","from":[480,200],"to":[480,350]} press, drag, release
{"action":"scroll","notches":6}                   turn the wheel
{"action":"stop"}                                 leave play mode
```

You get back:

1. **The sequence** — every frame of the action tiled into one numbered image.
   Tiling costs the same tokens as sending the frames loose, but a model compares
   two cells of one image far better than two separate images, and motion is a
   relationship *between* frames.
2. **The screen now** — full size, for reading text and measuring positions.

**Coordinates are in the picture's own pixels**, origin bottom-left. The bridge
scales them, so what you see is what you click and the real resolution is something
the agent never has to know.

## Telling it about your game

The bridge knows nothing about your game and should stay that way. Publish whatever
state you want an agent to see:

```csharp
PlaytestBridge.AddProbe("inputMode", () => $"\"{MyInput.Current}\"");
```

The callback returns **raw JSON**, so a subsystem can expose structure rather than
prose. It lands under `probes` in `PlaytestBridge.DumpState()`.

## Notes

- Stripped from release builds by `#if UNITY_EDITOR || DEVELOPMENT_BUILD`. It is an
  input injector and a state dumper; it must never ship in a player.
- Input is forced to `IgnoreFocus` so it still works while the editor is in the
  background. Without that the harness looks alive — screenshots keep arriving —
  while every keypress is silently dropped.
- One transport directory serves one editor. Running two projects at once? Set
  `UNITY_PLAYTEST_DIR` differently for each.
