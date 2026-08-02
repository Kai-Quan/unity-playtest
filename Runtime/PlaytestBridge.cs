#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Elegist.Playtest
{
    /// <summary>
    /// Lets an agent PLAY the game: inject real input, read the interactable
    /// surface, and capture what the screen actually shows.
    ///
    /// KNOWS NOTHING ABOUT YOUR GAME, deliberately. It can press keys, move a
    /// mouse and read the screen; anything game-specific it should be able to
    /// report is published by the game itself through <see cref="AddProbe"/>.
    /// That is the whole seam, and it is what makes this reusable.
    ///
    /// WHY VIRTUAL DEVICES, NOT DIRECT CALLS. Every action goes through
    /// Unity's Input System as a synthesized device event, so it travels the
    /// exact path a human's input does — bindings, action maps, and whatever
    /// gates the game puts in front of them. Poking components directly
    /// (button.onClick.Invoke(), setting fields) would bypass precisely the
    /// layer where input bugs live: an automated test that can click a button
    /// the player cannot reach is worse than no test at all.
    ///
    /// WHY IT SELF-BOOTSTRAPS. [RuntimeInitializeOnLoadMethod] means no scene
    /// wiring, so it works in every scene including ones built later, and
    /// nothing in the game holds a reference to it.
    ///
    /// STRIPPED FROM RELEASE BUILDS by the #if above — this is an input
    /// injector and a state dumper; it must never exist in a shipped player.
    ///
    /// CALLING IT (editor, during play mode) via coplay execute_script:
    ///     PlaytestBridge.Click(960, 540);
    ///     PlaytestBridge.Key("e");
    ///     PlaytestBridge.Screenshot("shot.png");   // → scratchpad-friendly path returned
    ///     PlaytestBridge.DumpState();              // → JSON string
    /// Input actions are frame-spanning coroutines: poll <see cref="Busy"/>
    /// (or just call DumpState a beat later) before asserting on the result.
    /// </summary>
    public class PlaytestBridge : MonoBehaviour
    {
        public static PlaytestBridge Instance { get; private set; }

        /// <summary>True while an injected gesture is still playing out across
        /// frames. Agents should wait for this to clear before asserting.</summary>
        public static bool Busy => Instance != null && Instance._busy;

        private Mouse _mouse;
        private Keyboard _keyboard;
        private bool _busy;
        private Vector2 _cursor;
        private string _lastScreenshot = "";

        /// <summary>Make sure the bridge exists. Called once at scene load, and
        /// again by anything that needs it — because a SCRIPT RECOMPILE DURING PLAY
        /// MODE reloads the domain and destroys this object, and the runtime-init
        /// hook does not fire a second time. Without this the game looks like it is
        /// still running while every command answers "not in play mode", and the
        /// only cure anyone finds is restarting the game.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("~PlaytestBridge");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            Instance = go.AddComponent<PlaytestBridge>();
        }

        private void Awake()
        {
            Instance = this;

            // MAKE INPUT SURVIVE AN UNFOCUSED EDITOR. Without this the bridge looks
            // half-broken in the most confusing way possible: screenshots keep
            // arriving, so the harness appears alive, while every injected click and
            // keypress is silently dropped.
            //
            // Two separate defaults conspire. The Input System disables
            // non-background devices when the application loses focus, and in the
            // editor it additionally routes device input to the Game View only while
            // that view has focus. An agent driving Unity from another process never
            // has focus, so it can never press a key — and a human watching would
            // see a game that simply ignores it.
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            // Named devices so they're identifiable in the Input Debugger, and
            // so we never fight over the real hardware's state.
            _mouse = InputSystem.AddDevice<Mouse>("PlaytestMouse");
            _keyboard = InputSystem.AddDevice<Keyboard>("PlaytestKeyboard");
            _cursor = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            Debug.Log("[PlaytestBridge] ready — virtual mouse + keyboard added.");
        }

        private void OnDestroy()
        {
            if (_mouse != null && _mouse.added) InputSystem.RemoveDevice(_mouse);
            if (_keyboard != null && _keyboard.added) InputSystem.RemoveDevice(_keyboard);
            if (Instance == this) Instance = null;
        }

        // ── Input injection ─────────────────────────────────────────────────

        /// <summary>Move the virtual cursor to a screen pixel (origin bottom-left).</summary>
        public static string MoveMouse(float x, float y)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            Instance._cursor = new Vector2(x, y);
            Instance.QueueMouse(false);
            return $"cursor -> ({x:0}, {y:0})";
        }

        /// <summary>Move to a point and left-click it. Press and release span
        /// separate frames so per-frame pollers (wasPressedThisFrame) see both.</summary>
        public static string Click(float x, float y)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            Instance._cursor = new Vector2(x, y);
            Instance.StartCoroutine(Instance.ClickRoutine(MouseButton.Left));
            return $"click queued at ({x:0}, {y:0})";
        }

        public static string RightClick(float x, float y)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            Instance._cursor = new Vector2(x, y);
            Instance.StartCoroutine(Instance.ClickRoutine(MouseButton.Right));
            return $"right-click queued at ({x:0}, {y:0})";
        }

        /// <summary>Hold a mouse button down across N frames then release —
        /// for gestures the game reads as "held" (RMB lean-in, drag-to-turn).</summary>
        public static string HoldDrag(float fromX, float fromY, float toX, float toY, int frames = 20)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            Instance.StartCoroutine(Instance.DragRoutine(
                new Vector2(fromX, fromY), new Vector2(toX, toY), Mathf.Max(2, frames)));
            return $"drag queued ({fromX:0},{fromY:0}) -> ({toX:0},{toY:0}) over {frames} frames";
        }

        /// <summary>Hold a key DOWN for a while, then release. Movement, and
        /// anything else the player does by holding rather than tapping, is
        /// untestable with a tap — a 3-second walk is not thirty taps of W.</summary>
        public static string HoldKey(string keyName, float seconds)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            if (!System.Enum.TryParse(keyName, true, out Key key))
                return $"ERR: unknown key '{keyName}'";
            Instance.StartCoroutine(Instance.HoldKeyRoutine(key, seconds));
            return $"holding '{key}' for {seconds:0.#}s";
        }

        /// <summary>Turn the wheel N notches (positive = zoom in). Required for the
        /// expedition: scroll-to-zoom IS the focus mechanic there, so a bridge that
        /// cannot scroll cannot test whether anything is photographable.</summary>
        public static string Scroll(int notches)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            if (notches == 0) return "ERR: zero notches";
            Instance.StartCoroutine(Instance.ScrollRoutine(notches));
            return $"{notches} scroll notch(es) queued";
        }

        /// <summary>Tap a key by name: "e", "escape", "space", "f", "q", "tab"…
        /// Matches the Key enum, case-insensitive.</summary>
        public static string Key(string keyName)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            if (!System.Enum.TryParse(keyName, true, out Key key))
                return $"ERR: unknown key '{keyName}' (use Input System Key names, e.g. E, Escape, Space)";
            Instance.StartCoroutine(Instance.KeyRoutine(key));
            return $"key '{key}' queued";
        }

        /// <summary>Type text into whatever currently has focus, as real text
        /// events (the path TMP_InputField listens on).</summary>
        public static string Type(string text)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            if (string.IsNullOrEmpty(text)) return "ERR: empty text";
            Instance.StartCoroutine(Instance.TypeRoutine(text));
            return $"typing {text.Length} char(s)";
        }

        // Queue ONLY — never InputSystem.Update() here. Forcing an input update
        // from outside the player loop processes press AND release inside a
        // single input frame, so the game's Update() never observes
        // wasPressedThisFrame and every injected click silently does nothing.
        // Letting Unity drain the queue on its own cadence (one event per
        // yielded frame) is what makes injected input indistinguishable from a
        // human's.
        private void QueueMouse(bool leftDown, bool rightDown = false)
        {
            var state = new MouseState { position = _cursor };
            state = state.WithButton(MouseButton.Left, leftDown);
            state = state.WithButton(MouseButton.Right, rightDown);
            InputSystem.QueueStateEvent(_mouse, state);
        }

        private IEnumerator ClickRoutine(MouseButton button)
        {
            _busy = true;
            bool left = button == MouseButton.Left;
            QueueMouse(false);                       // land the cursor first — hover resolves
            yield return null;
            QueueMouse(left, !left);                 // press
            yield return null;
            yield return null;                       // hold one extra frame; some pollers are late
            QueueMouse(false);                       // release
            yield return null;
            _busy = false;
        }

        private IEnumerator DragRoutine(Vector2 from, Vector2 to, int frames)
        {
            _busy = true;
            _cursor = from;
            QueueMouse(false);
            yield return null;
            QueueMouse(true);
            yield return null;
            for (int i = 1; i <= frames; i++)
            {
                Vector2 next = Vector2.Lerp(from, to, i / (float)frames);
                Vector2 step = next - _cursor;
                _cursor = next;
                // delta must be sent EXPLICITLY: a Mouse device does not derive it
                // from position changes, so a drag built only from positions reads
                // as zero movement. Everything that listens on delta rather than
                // position — look, and the diorama's drag-to-turn — would silently
                // do nothing.
                InputSystem.QueueStateEvent(_mouse, new MouseState { position = _cursor, delta = step }
                    .WithButton(MouseButton.Left, true));
                yield return null;
            }
            QueueMouse(false);
            yield return null;
            _busy = false;
        }

        private IEnumerator ScrollRoutine(int notches)
        {
            _busy = true;
            float dir = Mathf.Sign(notches);
            for (int i = 0; i < Mathf.Abs(notches); i++)
            {
                // 120 per notch is the platform convention the Input System
                // normalises against; the game only reads the sign, but sending a
                // realistic magnitude keeps this honest if that ever changes.
                var state = new MouseState { position = _cursor, scroll = new Vector2(0f, dir * 120f) };
                InputSystem.QueueStateEvent(_mouse, state);
                yield return null;
                // Zero it again, or the wheel reads as held and zoom runs away.
                InputSystem.QueueStateEvent(_mouse, new MouseState { position = _cursor });
                yield return null;
            }
            _busy = false;
        }

        private IEnumerator HoldKeyRoutine(Key key, float seconds)
        {
            _busy = true;
            // Re-send the held state every frame. A single down event is enough for
            // wasPressedThisFrame, but anything reading isPressed needs the key to
            // keep being held, and that is most of what movement does.
            float t = 0f;
            while (t < seconds)
            {
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            _busy = false;
        }

        private IEnumerator KeyRoutine(Key key)
        {
            _busy = true;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return null;
            _busy = false;
        }

        private IEnumerator TypeRoutine(string text)
        {
            _busy = true;
            foreach (char c in text)
            {
                InputSystem.QueueTextEvent(_keyboard, c);
                yield return null;
            }
            _busy = false;
        }

        // ── Screenshot ──────────────────────────────────────────────────────

        /// <summary>Capture the composited frame (world + all UI) to a PNG.
        /// Relative paths land under the project root. Poll
        /// <see cref="LastScreenshot"/> — the write completes a frame later.</summary>
        public static string Screenshot(string path)
        {
            if (Instance == null) return "ERR: bridge not running (enter play mode)";
            if (string.IsNullOrEmpty(path)) path = "playtest.png";
            string full = System.IO.Path.IsPathRooted(path)
                ? path
                : System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), path);
            Instance.StartCoroutine(Instance.ShotRoutine(full));
            return full;
        }

        public static string LastScreenshot => Instance != null ? Instance._lastScreenshot : "";

        /// <summary>Capture the composited frame as a texture rather than a file.
        /// MUST be called from a coroutine AFTER `yield return new
        /// WaitForEndOfFrame()`; anything earlier reads a half-drawn back buffer.
        /// The caller owns the result and must Destroy it.
        ///
        /// Frames are wanted in memory because several of them get tiled into one
        /// contact sheet — writing each to disk first only to read it back would be
        /// work done twice.</summary>
        public static Texture2D GrabFrame(int longEdge)
        {
            if (Instance == null) return null;
            var full = ScreenCapture.CaptureScreenshotAsTexture();
            int edge = Mathf.Max(full.width, full.height);
            if (edge <= longEdge) return full;

            float s = longEdge / (float)edge;
            var small = Resize(full,
                Mathf.Max(1, Mathf.RoundToInt(full.width * s)),
                Mathf.Max(1, Mathf.RoundToInt(full.height * s)));
            Destroy(full);
            return small;
        }

        /// <summary>Rescale through a temporary RT so the result stays readable —
        /// a point-sampled half-size frame loses exactly the thin UI text a
        /// playtester needs to read.</summary>
        public static Texture2D Resize(Texture2D src, int w, int h)
        {
            // LINEAR, NOT DEFAULT. A screenshot is already sRGB-encoded display
            // pixels; the only thing wanted here is a resample. A default render
            // texture in a linear-space project converts on write, so the resize
            // silently applied a gamma curve and every screenshot came back with
            // its shadows lifted — a night scene lit by one lamp photographed as an
            // evenly-lit room, which is a lie about the game, and worse, a lie an
            // agent judging the art has no way to detect.
            //
            // The contact sheet resamples a second time to fill its cells, so the
            // sheet was getting it twice.
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.Linear);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var dst = new Texture2D(w, h, TextureFormat.RGB24, false);
            dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            dst.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return dst;
        }

        /// <summary>Long edge of a written screenshot. Half-resolution is a
        /// deliberate economy: an agent's screenshots are its context budget, and a
        /// full 1920x1080 frame costs roughly four times as many tokens as this one
        /// while showing nothing a playtester needs. Halving it roughly doubles how
        /// long an agent can keep playing before it runs out of room — and a
        /// playtest that dies before reaching the end of the scene is worth less
        /// than one that saw slightly softer pictures.</summary>
        public static int ScreenshotLongEdge = 960;

        private IEnumerator ShotRoutine(string fullPath)
        {
            _busy = true;
            // Must be end-of-frame: CaptureScreenshotAsTexture reads the back
            // buffer, so anything earlier grabs a half-drawn frame.
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                var dir = System.IO.Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllBytes(fullPath, Encode(tex));
                _lastScreenshot = fullPath;
                Debug.Log($"[PlaytestBridge] screenshot -> {fullPath}");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PlaytestBridge] screenshot failed: {e.Message}");
            }
            finally
            {
                Destroy(tex);
                _busy = false;
            }
        }

        /// <summary>Downscale before writing.</summary>
        private static byte[] Encode(Texture2D full)
        {
            int longEdge = Mathf.Max(full.width, full.height);
            if (longEdge <= ScreenshotLongEdge) return full.EncodeToPNG();

            float scale = ScreenshotLongEdge / (float)longEdge;
            var small = Resize(full,
                Mathf.Max(1, Mathf.RoundToInt(full.width * scale)),
                Mathf.Max(1, Mathf.RoundToInt(full.height * scale)));
            byte[] png = small.EncodeToPNG();
            Destroy(small);
            return png;
        }

        // ── State dump ──────────────────────────────────────────────────────

        /// <summary>JSON snapshot of everything an agent needs to decide its
        /// next action: control state, what's on screen, and where to click.
        /// Structure over pixels — the agent shouldn't be guessing from images
        /// what the scene graph can state exactly.</summary>
        public static string DumpState()
        {
            if (Instance == null) return "{\"error\":\"bridge not running (enter play mode)\"}";
            return Instance.BuildState();
        }

        private string BuildState()
        {
            var sb = new StringBuilder("{\n");
            sb.Append($"  \"scene\": \"{Esc(SceneManager.GetActiveScene().name)}\",\n");
            sb.Append($"  \"screen\": [{Screen.width}, {Screen.height}],\n");
            sb.Append($"  \"busy\": {(_busy ? "true" : "false")},\n");
            sb.Append($"  \"timeScale\": {Time.timeScale},\n");

            sb.Append($"  \"cursorVisible\": {(Cursor.visible ? "true" : "false")},\n");
            sb.Append($"  \"cursorLock\": \"{Cursor.lockState}\",\n");
            sb.Append($"  \"virtualCursor\": [{_cursor.x:0}, {_cursor.y:0}],\n");

            // Clickable uGUI buttons with screen-space centers — the agent can
            // feed these straight back into Click(). This is as far as a
            // game-agnostic dump can go on its own; everything specific to YOUR
            // game arrives through AddProbe.
            sb.Append("  \"buttons\": [\n");
            bool first = true;
            foreach (var btn in FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (btn == null || !btn.interactable || !btn.gameObject.activeInHierarchy) continue;
                if (!TryScreenCenter(btn.transform as RectTransform, out Vector2 c)) continue;
                if (c.x < 0 || c.y < 0 || c.x > Screen.width || c.y > Screen.height) continue; // off-screen

                string label = "";
                var tmp = btn.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null) label = tmp.text;

                if (!first) sb.Append(",\n");
                sb.Append($"    {{\"name\": \"{Esc(btn.name)}\", \"label\": \"{Esc(label)}\", \"at\": [{c.x:0}, {c.y:0}]}}");
                first = false;
            }
            sb.Append("\n  ],\n");

            // Free-form probes other systems contribute (see AddProbe). Values
            // are embedded as RAW JSON so a subsystem can publish structure —
            // e.g. the diorama's world-space clickables with screen positions,
            // which this project needs far more than uGUI buttons.
            sb.Append("  \"probes\": {");
            first = true;
            foreach (var kv in _probes)
            {
                string val;
                try { val = kv.Value(); } catch (System.Exception e) { val = $"\"ERR: {Esc(e.Message)}\""; }
                if (string.IsNullOrEmpty(val)) val = "null";
                if (!first) sb.Append(", ");
                sb.Append($"\n    \"{Esc(kv.Key)}\": {val}");
                first = false;
            }
            sb.Append(first ? "},\n" : "\n  },\n");

            sb.Append($"  \"lastScreenshot\": \"{Esc(_lastScreenshot.Replace('\\', '/'))}\"\n");
            sb.Append("}");
            return sb.ToString();
        }

        private static readonly Dictionary<string, System.Func<string>> _probes =
            new Dictionary<string, System.Func<string>>();

        /// <summary>Let any system publish into the state dump. The callback must
        /// return VALID JSON (a quoted string, number, array or object) — it is
        /// embedded raw, so subsystems can expose structure rather than prose.
        /// Keeps the bridge ignorant of every game system while still letting an
        /// agent see world-space clickables, plate counts, quotas, etc.
        ///
        /// Register from a scene-scoped service's Awake; safe to call when the
        /// bridge isn't running (release builds compile this whole file out, so
        /// guard call sites with the same #if).</summary>
        public static void AddProbe(string key, System.Func<string> read)
        {
            if (string.IsNullOrEmpty(key) || read == null) return;
            _probes[key] = read;
        }

        /// <summary>Screen-space center of a world point, or null when it's
        /// behind the camera. Helper for probes publishing clickable world
        /// objects — the agent feeds the result straight back into Click().</summary>
        public static bool TryWorldToScreen(Vector3 world, out Vector2 screen)
        {
            screen = default;
            var cam = Camera.main;
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return false;
            screen = new Vector2(sp.x, sp.y);
            return true;
        }

        /// <summary>JSON-escape helper for probe authors.</summary>
        public static string EscapeJson(string s) => Esc(s);

        private static bool TryScreenCenter(RectTransform rt, out Vector2 center)
        {
            center = default;
            if (rt == null) return false;
            var canvas = rt.GetComponentInParent<Canvas>();
            if (canvas == null) return false;

            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector3 mid = (corners[0] + corners[2]) * 0.5f;

            // Overlay canvases already live in screen space; everything else
            // needs projecting through the canvas's own camera.
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                center = new Vector2(mid.x, mid.y);
                return true;
            }
            Camera cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(mid);
            if (sp.z <= 0f) return false;   // behind the lens
            center = new Vector2(sp.x, sp.y);
            return true;
        }

        private static string Esc(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
    }
}
#endif
