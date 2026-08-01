#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Elegist.Playtest
{
    /// <summary>
    /// A HUMAN-LEVEL play interface, for agents that should actually play the game
    /// rather than script it.
    ///
    /// One declarative action in, a series of screenshots out:
    ///
    ///     {"action":"key",   "key":"w", "seconds":3}
    ///     {"action":"click", "at":[960, 540]}
    ///     {"action":"scroll","notches":6}
    ///     {"action":"drag",  "from":[960,400], "to":[960,700], "seconds":0.4}
    ///     {"action":"wait",  "seconds":2}
    ///     {"action":"look"}
    ///
    /// EVERY ACTION IS FILMED, NOT SNAPSHOTTED. The input is the fast part; the
    /// game's reaction to it is not. So after every action — not just the long
    /// ones — this keeps photographing until the picture stops changing, and hands
    /// back the whole run. `watch` sets how long it will wait for a slow reaction
    /// and `fps` how finely it samples, because only the caller knows whether it
    /// nudged something or set a long thing in motion.
    ///
    /// EVERY ACTION ANSWERS IN TWO PICTURES. A contact sheet of the whole gesture
    /// — before it, during it, after it, numbered in order — and then the screen
    /// as it stands now, full size. Motion is a relationship between frames, and
    /// tiling them puts both halves of that relationship inside one image where a
    /// model can actually compare them; the separate full-size frame is the one
    /// whose text gets read and whose positions get clicked. Loose frames did
    /// neither job well and paid full price for both.
    ///
    /// COORDINATES ARE IN THE COORDINATE SPACE OF THE PICTURES. The screenshots
    /// are smaller than the real window, so an agent reading a position off one
    /// and passing it back would land somewhere else entirely. Rather than warn
    /// about the arithmetic, this scales for the caller: what you see is what you
    /// click, and the real resolution is something an agent never has to know.
    ///
    /// THE VOCABULARY DELIBERATELY EXPOSES NO INTERNAL NAMES. No GameObjects, no
    /// facet ids, no scene paths — only what a person at the keyboard has:
    /// coordinates, keys, and their own eyes. An agent that can click
    /// "Evidence_Diary" by name cannot discover that the diary is impossible to
    /// SEE, and that failure is exactly the kind a hand-written regression test is
    /// blind to. Aim by reading the previous screenshot; every result reports the
    /// screen size so coordinates mean something.
    ///
    /// Actions span frames, so this is start-then-collect:
    ///     PlaytestAction.Begin(json)   -> "running"
    ///     PlaytestAction.Result()      -> JSON once done
    /// An MCP wrapper should poll Result() internally and hand the agent one
    /// tool call whose payload embeds the images inline. That is the real prize:
    /// screenshots the agent SEES without choosing to open them.
    /// </summary>
    public static class PlaytestAction
    {
        public static string LastResult = "{\"ok\":false,\"error\":\"nothing run yet\"}";
        public static bool Running;

        private static int _seq;

        public static string Begin(string json)
        {
            PlaytestBridge.Ensure();
            if (PlaytestBridge.Instance == null)
                return "{\"ok\":false,\"error\":\"not in play mode\"}";
            if (Running) return "{\"ok\":false,\"error\":\"an action is still running\"}";

            var a = Parse(json);
            if (a == null) return "{\"ok\":false,\"error\":\"could not parse action\"}";

            Running = true;
            LastResult = "";
            PlaytestBridge.Instance.StartCoroutine(Run(a));
            return "running";
        }

        public static string Result() => Running ? "running" : LastResult;

        // ── the action ──────────────────────────────────────────────────

        private class Act
        {
            public string action = "look";
            public string key;
            public float seconds;
            public int notches;
            public float x, y;
            public float fromX, fromY, toX, toY;
            public bool hasFrom;
            /// <summary>How many movements to break a drag into. NOT a picture
            /// count — n steps photograph n-1 moments between them, and calling it
            /// "frames" invited people to read it as "give me n pictures".</summary>
            public int steps;
            /// <summary>How long to keep watching AFTER the input, and how often
            /// to sample while watching. Exposed because only the agent knows
            /// whether it just nudged something or set a long thing in motion.</summary>
            public float watch = 1.2f;
            public float fps = 5f;
        }

        /// <summary>Nine cells is a 3x3 sheet, which is as many as stays legible.</summary>
        private const int MaxFrames = 9;

        private static IEnumerator Run(Act a)
        {
            var frames = new List<Texture2D>();
            var times = new List<float>();
            var log = new StringBuilder();

            // Positions arrive in picture space; the game wants window pixels.
            float k = ShotToScreen();
            float t0 = Time.realtimeSinceStartup;

            yield return Capture(frames, times, t0);

            switch (a.action)
            {
                case "key":
                    if (a.seconds > 0.05f)
                    {
                        // Hold, sampling as it goes so the agent sees the middle of
                        // the action and not only its end. Six samples is what fits
                        // a legible grid; a longer hold just spaces them wider.
                        PlaytestBridge.HoldKey(a.key, a.seconds);
                        float step = Mathf.Clamp(a.seconds / 6f, 0.25f, 1f);
                        for (float t = 0f; t < a.seconds; t += step)
                        {
                            yield return new WaitForSecondsRealtime(Mathf.Min(step, a.seconds - t));
                            yield return Capture(frames, times, t0);
                        }
                    }
                    else
                    {
                        PlaytestBridge.Key(a.key);
                    }
                    log.Append($"pressed '{a.key}'");
                    if (a.seconds > 0.05f) log.Append($" for {a.seconds:0.#}s");
                    break;

                case "click":
                    PlaytestBridge.MoveMouse(a.x * k, a.y * k);
                    yield return null;
                    PlaytestBridge.Click(a.x * k, a.y * k);
                    yield return WaitForInput();
                    log.Append($"clicked ({a.x:0}, {a.y:0})");
                    break;

                case "move":
                    PlaytestBridge.MoveMouse(a.x * k, a.y * k);
                    log.Append($"moved the cursor to ({a.x:0}, {a.y:0})");
                    break;

                case "scroll":
                    PlaytestBridge.Scroll(a.notches);
                    yield return WaitForInput();
                    log.Append($"scrolled {a.notches} notch(es)");
                    break;

                case "drag":
                    {
                        // A drag is the one gesture whose MIDDLE is the interesting
                        // part — turning an object in the hand looks like nothing at
                        // either end. Break it into legs and shoot between them, so
                        // the agent sees the thing rotating rather than two stills
                        // that could equally be no movement at all.
                        int legs = Mathf.Clamp(a.steps > 0 ? a.steps : 5, 1, 8);
                        float dur = a.seconds > 0.05f ? a.seconds : 0.5f;
                        for (int i = 0; i < legs; i++)
                        {
                            float s0 = i / (float)legs, s1 = (i + 1) / (float)legs;
                            PlaytestBridge.HoldDrag(
                                Mathf.Lerp(a.fromX, a.toX, s0) * k, Mathf.Lerp(a.fromY, a.toY, s0) * k,
                                Mathf.Lerp(a.fromX, a.toX, s1) * k, Mathf.Lerp(a.fromY, a.toY, s1) * k,
                                Mathf.Max(4, Mathf.RoundToInt(dur / legs * 60f)));
                            yield return WaitForInput();
                            if (i < legs - 1) yield return Capture(frames, times, t0);
                        }
                        log.Append($"dragged ({a.fromX:0},{a.fromY:0}) to ({a.toX:0},{a.toY:0})");
                    }
                    break;

                case "wait":
                    yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, a.seconds));
                    log.Append($"waited {a.seconds:0.#}s");
                    break;

                default:
                    log.Append("looked");
                    break;
            }

            yield return Watch(frames, times, t0, a.watch, a.fps);

            LastResult = Publish(frames, times, log.ToString());
            foreach (var f in frames) Object.Destroy(f);
            Running = false;
        }

        /// <summary>Screen pixels per screenshot pixel — the one number that keeps
        /// the agent's coordinate space and its eyes in agreement.</summary>
        private static float ShotToScreen()
        {
            int edge = Mathf.Max(Screen.width, Screen.height);
            return edge <= PlaytestBridge.ScreenshotLongEdge
                ? 1f
                : edge / (float)PlaytestBridge.ScreenshotLongEdge;
        }

        /// <summary>Write the sequence and the final frame, and describe both. The
        /// captions matter as much as the pixels: a sheet nobody knows how to read
        /// is just a small blurry screenshot.</summary>
        private static string Publish(List<Texture2D> frames, List<float> times, string did)
        {
            if (frames.Count == 0)
                return "{\"ok\": false, \"error\": \"captured nothing — is the game still running?\"}";

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playtest");
            System.IO.Directory.CreateDirectory(dir);
            _seq++;

            var last = frames[frames.Count - 1];
            var shots = new List<(string path, string caption)>();

            // The sheet is the JOURNEY and the full-size picture is where it ended.
            // They used to overlap — the final moment was paid for twice, once
            // shrunk into the last cell and once at full size — so the last frame
            // is left out of the sheet and the picture below it finishes the run.
            if (frames.Count > 1)
            {
                int m = frames.Count - 1;
                string sheet = System.IO.Path.Combine(dir, $"{_seq:000}_sequence.png");
                System.IO.File.WriteAllBytes(sheet,
                    PlaytestContactSheet.Compose(frames.GetRange(0, m)));

                var when = new StringBuilder();
                for (int i = 0; i < m; i++)
                    when.Append(i == 0 ? "" : ", ").Append($"{times[i]:0.0}s");
                shots.Add((sheet,
                    $"HOW IT GOT THERE — the first {m} frame(s) of the action, in order, left " +
                    $"to right then down, numbered 1-{m}. Frame 1 is before the action. " +
                    $"Taken at {when}. The full-size picture below is the next and last frame, " +
                    "so read the two together. Read MOVEMENT here; measure positions there."));
            }

            string now = System.IO.Path.Combine(dir, $"{_seq:000}_now.png");
            System.IO.File.WriteAllBytes(now, last.EncodeToPNG());
            shots.Add((now, $"WHERE IT ENDED — the screen now, full size, at {times[times.Count - 1]:0.0}s. " +
                            "Aim off this one; the numbers you read here are the numbers to pass back."));

            var sb = new StringBuilder("{");
            sb.Append("\"ok\": true, ");
            sb.Append($"\"did\": \"{PlaytestBridge.EscapeJson(did)}\", ");
            sb.Append($"\"screen\": [{last.width}, {last.height}], ");
            sb.Append("\"shots\": [");
            for (int i = 0; i < shots.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{{\"path\": \"{PlaytestBridge.EscapeJson(shots[i].path.Replace('\\', '/'))}\", ");
                sb.Append($"\"caption\": \"{PlaytestBridge.EscapeJson(shots[i].caption)}\"}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Wait for the injected gesture to finish being TYPED. StartCoroutine
        /// runs a coroutine up to its first yield immediately, so Busy is already set
        /// by the time the bridge call returns and this cannot race.</summary>
        private static IEnumerator WaitForInput()
        {
            while (PlaytestBridge.Busy) yield return null;
        }

        /// <summary>Keep photographing until the game stops reacting.
        ///
        /// WHY THIS EXISTS. Waiting for the input to finish is not the same as
        /// waiting for the game to finish. A click is over in four frames; the
        /// camera move it triggers takes most of a second. Every "after" shot used
        /// to be taken during that move, so an agent saw a camera part-way to
        /// somewhere and concluded — reasonably — that nothing there was clickable.
        ///
        /// The stop condition costs nothing extra: Capture already refuses to add a
        /// frame that looks like the one before it, so "no frame was added" IS "the
        /// picture has stopped changing". Two of those in a row and we are done.
        ///
        /// It will not stop before the game has moved at all, or a click that takes
        /// a moment to respond would be photographed only in the pause before it
        /// does — which is the bug this fixes, arrived at from the other side.</summary>
        private static IEnumerator Watch(List<Texture2D> frames, List<float> times,
                                         float t0, float seconds, float fps)
        {
            seconds = Mathf.Clamp(seconds, 0f, 10f);
            float step = 1f / Mathf.Clamp(fps, 1f, 20f);
            bool moved = false;
            int still = 0;

            for (float t = 0f; t < seconds; t += step)
            {
                yield return new WaitForSecondsRealtime(step);

                int had = frames.Count;
                yield return Capture(frames, times, t0);

                if (frames.Count > had) { moved = true; still = 0; }
                else if (moved && ++still >= 2) yield break;
            }
        }

        private static IEnumerator Capture(List<Texture2D> into, List<float> times, float t0)
        {
            // End of frame, or the capture reads a half-drawn back buffer.
            yield return new WaitForEndOfFrame();
            var tex = PlaytestBridge.GrabFrame(PlaytestBridge.ScreenshotLongEdge);
            if (tex == null) yield break;

            // Drop a frame that looks like the one before it. Every cell an agent
            // receives costs context it could have spent playing longer, and a
            // duplicate teaches it nothing — "nothing changed" is better said once
            // in words than shown twice.
            if (into.Count > 0 && LooksSame(into[into.Count - 1], tex))
            {
                Object.Destroy(tex);
                yield break;
            }

            // Past nine cells the sheet stops being legible. Forget the oldest
            // MIDDLE frame rather than refusing new ones: the first and the latest
            // are the two an agent actually reasons from.
            if (into.Count >= MaxFrames)
            {
                Object.Destroy(into[1]);
                into.RemoveAt(1);
                times.RemoveAt(1);
            }

            into.Add(tex);
            times.Add(Time.realtimeSinceStartup - t0);
        }

        /// <summary>Has the picture stopped changing?
        ///
        /// Not exact equality — an HDRP frame is never bit-identical twice running,
        /// so an exact test says "still moving" forever and the sequence fills up
        /// with six photographs of a stationary room. This samples every third
        /// pixel and asks whether a tenth of a percent of them actually moved,
        /// which ignores dithering and temporal noise while still catching anything
        /// a person would see. It is a threshold on a subtraction, and calling it
        /// computer vision would be flattering it.</summary>
        private static bool LooksSame(Texture2D a, Texture2D b)
        {
            if (a == null || b == null || a.width != b.width || a.height != b.height) return false;
            var pa = a.GetRawTextureData<byte>();
            var pb = b.GetRawTextureData<byte>();
            if (pa.Length != pb.Length) return false;

            int looked = 0, differing = 0;
            for (int i = 0; i < pa.Length; i += 9)
            {
                looked++;
                if (Mathf.Abs(pa[i] - pb[i]) > 8) differing++;
            }
            return differing * 1000 < looked;
        }

        // ── tiny JSON reader ────────────────────────────────────────────
        // Deliberately hand-rolled: this takes flat objects of strings and
        // numbers, and pulling in a parser for that would be the larger cost.

        private static Act Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var a = new Act();
            a.action = Str(json, "action") ?? "look";
            a.key = Str(json, "key");
            a.seconds = Num(json, "seconds", Num(json, "durationInSeconds", 0f));
            a.notches = Mathf.RoundToInt(Num(json, "notches", 0f));
            a.steps = Mathf.RoundToInt(Num(json, "steps", 0f));
            a.watch = Num(json, "watch", 1.2f);
            a.fps = Num(json, "fps", 5f);
            a.x = Num(json, "x", 0f);
            a.y = Num(json, "y", 0f);

            // A point is a point. Drag needs two of them so it takes [x,y] pairs,
            // and an agent that has learned that shape should not have to learn a
            // second one to click. "at" is the same thing spelled the same way.
            var at = Pair(json, "at");
            if (at.HasValue) { a.x = at.Value.x; a.y = at.Value.y; }

            // "keyPress" is accepted as a friendlier alias for {"action":"key"}.
            var kp = Str(json, "keyPress");
            if (!string.IsNullOrEmpty(kp)) { a.action = "key"; a.key = kp; }

            var from = Pair(json, "from");
            var to = Pair(json, "to");
            if (from.HasValue && to.HasValue)
            {
                a.hasFrom = true;
                a.fromX = from.Value.x; a.fromY = from.Value.y;
                a.toX = to.Value.x; a.toY = to.Value.y;
                if (a.action == "look") a.action = "drag";
            }
            return a;
        }

        private static string Str(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int c = json.IndexOf(':', i); if (c < 0) return null;
            int q1 = json.IndexOf('"', c); if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1); if (q2 < 0) return null;
            // Reject a number that happens to follow — only quoted values count.
            for (int k = c + 1; k < q1; k++) if (!char.IsWhiteSpace(json[k])) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static float Num(string json, string key, float fallback)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int c = json.IndexOf(':', i); if (c < 0) return fallback;
            var sb = new StringBuilder();
            for (int k = c + 1; k < json.Length; k++)
            {
                char ch = json[k];
                if (char.IsWhiteSpace(ch)) { if (sb.Length > 0) break; continue; }
                if (char.IsDigit(ch) || ch == '-' || ch == '.') sb.Append(ch);
                else break;
            }
            return float.TryParse(sb.ToString(), out float v) ? v : fallback;
        }

        private static Vector2? Pair(string json, string key)
        {
            int i = json.IndexOf($"\"{key}\"", System.StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int open = json.IndexOf('[', i); if (open < 0) return null;
            int close = json.IndexOf(']', open); if (close < 0) return null;
            var parts = json.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length < 2) return null;
            if (!float.TryParse(parts[0].Trim(), out float x)) return null;
            if (!float.TryParse(parts[1].Trim(), out float y)) return null;
            return new Vector2(x, y);
        }
    }
}
#endif
