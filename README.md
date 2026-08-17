# Playtest

Lets an AI agent play your Unity game the way a person does, and see what happened.

It presses keys and moves a mouse through the **real Input System** as synthesized
device events, so every action travels the same path a player's does — bindings,
action maps, and whatever gates the game puts in front of them. Nothing here can
click a button the player cannot reach, which is the entire point: a test that can
reach further than a player is worse than no test.

Three tools, and the split between them is the design:

| | question it answers |
|---|---|
| **`play`** | what does a player experience |
| **`inspect`** | what is actually in the scene — a developer's camera |
| **`snapshot`** | where is everything, as numbers |

## Install

Unity → **Window → Package Manager → + → Install package from git URL**:

```
https://github.com/Kai-Quan/unity-playtest.git
```

or pin a release:

```
https://github.com/Kai-Quan/unity-playtest.git#v0.1.0
```

or add it to `Packages/manifest.json` directly:

```json
"com.kaiquan.playtest": "https://github.com/Kai-Quan/unity-playtest.git#v0.1.0"
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

## Looking at the scene

`play` answers what a player sees. It cannot answer whether a prop is floating,
because a floating prop looks fine from the one angle the player happens to have.
`inspect` is the developer's camera for exactly those questions.

```
{"tool":"inspect","action":"orbit","target":"Desk","angles":4}   a ring of angles
{"tool":"inspect","action":"plan","from":"top","target":"Desk"}  orthographic
{"tool":"inspect","action":"frame","target":"Desk","yaw":35}     one angle
```

**`orbit` is usually what you want.** Whatever stands in front of a thing from here
is exactly what you cannot see past, and you do not know it is there until you have
moved — so one angle cannot answer "show me this object".

**`plan` is how you settle whether two things are touching.** Perspective is what
makes that unanswerable from any normal angle; an orthographic view answers it in
one picture.

It works during play mode too, where it shows the live scene — the only way to see
where something instantiated at runtime actually ended up. **Do not give it to an
agent you have asked to playtest blind:** it sees through walls, and the only thing
that report is worth comes from being unable to.

## Reading the scene as numbers

If a person edits the scene by hand and an agent also works on it, neither can see
what the other did. `snapshot` is the shared record.

```
{"tool":"snapshot","root":"Level_01","out":"../design/level01.json"}
{"tool":"snapshot","action":"diff","root":"Level_01","out":"../design/level01.json"}
```

`write` records every transform under a root, plus which mesh each object wears.
`diff` reports **only what moved, was added or was deleted** since — which is the
question you actually have when you come back to a scene someone else has touched.

A snapshot is a **record, never an instruction**. Nothing here writes to the scene.
That is the whole point: the generator-shaped alternative — a script holding every
coordinate and re-asserting it — silently destroys hand edits the first time a
person drags something.

It refuses during play mode, unlike `inspect`, and for the opposite reason. In play
every transform is whatever the game has done to it since, and a snapshot of that
would look correct and be wrong.

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

## Known issues

**Pointer position is not injected.** `Mouse.current.position` reads `(0,0)`
whatever coordinate is sent, and a Mouse/Keyboard device pair leaks on every action,
so `Mouse.current` ends up not being the device the click reaches. Keys and
key-driven actions work; anything needing a cursor position — hover, uGUI buttons,
world-space raycast clicks — does not. Being fixed.

## Licence

MIT — see [LICENSE.md](LICENSE.md).
