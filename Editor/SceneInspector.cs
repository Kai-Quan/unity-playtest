using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Elegist.Playtest.EditorTools
{
    /// <summary>
    /// The DEVELOPER's camera, as a tool an agent can drive — the counterpart to
    /// `play`, which is the player's.
    ///
    /// WHY IT REFUSES DURING PLAY MODE. The playtester's entire value is that it
    /// cannot see what a player cannot see; a flying camera would destroy that, and
    /// a rule in its brief saying "do not use inspect" is a rule that can be
    /// forgotten. The playtester lives in play mode and this lives outside it, so
    /// the two can never overlap. Staging questions — does it float, does it
    /// intersect, is it the right size — are edit-mode questions anyway.
    ///
    ///     {"action":"look"}                                 shoot from where you are
    ///     {"action":"turn","yaw":30,"pitch":-10}            look around, camera stays put
    ///     {"action":"pan","right":1,"up":0.5,"forward":2}   slide, angle unchanged
    ///     {"action":"zoom","by":1}                          closer (negative backs off)
    ///     {"action":"frame","target":"Organ","yaw":35}      one object, one angle
    ///     {"action":"orbit","target":"Organ","angles":4}    ring around it, one sheet
    ///     {"action":"plan","from":"top","target":"Desk"}    orthographic plan/elevation
    ///
    /// SEVEN VERBS RATHER THAN THREE WITH PARAMETERS, because a capability nobody
    /// can see is a capability nobody has. Turning and strafing were both possible
    /// in the first draft, inside a verb called "fly", and the first person to read
    /// it asked where they were.
    /// </summary>
    public static class SceneInspector
    {
        /// <summary>Bigger cells than a playtest sequence. That sheet answers "did
        /// it move", which survives being small; this one answers "is that gap
        /// real", which does not.</summary>
        private const int CellWidth = 640;

        private static int _seq;

        /// <summary>Lift the exposure of every capture so a dark scene can still be
        /// measured. Off with {"brighten":0} when the lighting itself is the thing
        /// being judged.</summary>
        private static bool _brighten = true;

        public static string Run(string json)
        {
            _brighten = PlaytestJson.Num(json, "brighten", 1f) > 0.5f;
            if (EditorApplication.isPlaying)
                return Fail("inspect is an edit-mode tool — stop play mode first. While the " +
                            "game is running, use `play` and look with the player's eyes.");

            var view = SceneView.lastActiveSceneView;
            if (view == null)
                return Fail("no Scene View is open in the editor, and nothing here can open one. " +
                            "Ask whoever is at the machine for the Scene tab, then try again.");

            switch (PlaytestJson.Str(json, "action") ?? "look")
            {
                case "turn": return Turn(view, json);
                case "pan": return Pan(view, json);
                case "zoom": return Zoom(view, json);
                case "frame": return Frame(view, json);
                case "orbit": return Orbit(view, json);
                case "plan": return Plan(view, json);
                default: return Shoot(view, "the Scene View as it stands");
            }
        }

        // ── moving ──────────────────────────────────────────────────────

        /// <summary>Look around WITHOUT the camera moving.
        ///
        /// The Scene View stores a pivot and an orbit distance, so setting rotation
        /// alone swings the camera sideways around a point in front of it — useful,
        /// but not what "turn the camera" means to a person. Keeping the camera
        /// still means moving the pivot to follow the new facing.</summary>
        private static string Turn(SceneView view, string json)
        {
            Vector3 eye = Eye(view);
            Vector3 e = view.rotation.eulerAngles;
            view.rotation = Quaternion.Euler(
                Mathf.Clamp(Wrap(e.x) + PlaytestJson.Num(json, "pitch", 0f), -89f, 89f),
                e.y + PlaytestJson.Num(json, "yaw", 0f), 0f);
            view.pivot = eye + view.rotation * Vector3.forward * Distance(view);

            return Shoot(view, $"turned to face {view.rotation.eulerAngles.y:0}° " +
                               $"({Wrap(view.rotation.eulerAngles.x):0}° from level)");
        }

        /// <summary>Slide the camera, keeping the angle. Axes are the camera's own,
        /// so "right" is right on screen — which is the only sense that is useful
        /// when you are looking at a picture and want to see slightly past
        /// something.</summary>
        private static string Pan(SceneView view, string json)
        {
            Vector3 step = view.rotation * new Vector3(
                PlaytestJson.Num(json, "right", 0f),
                PlaytestJson.Num(json, "up", 0f),
                PlaytestJson.Num(json, "forward", 0f));
            view.pivot += step;
            return Shoot(view, $"slid {step.magnitude:0.00}m without turning");
        }

        /// <summary>Dolly in or out. One unit halves the field, so it reads like a
        /// scroll wheel rather than a number nobody can predict.</summary>
        private static string Zoom(SceneView view, string json)
        {
            view.size = Mathf.Clamp(view.size / Mathf.Pow(2f, PlaytestJson.Num(json, "by", 1f)),
                                    0.05f, 5000f);
            return Shoot(view, $"field is now {view.size:0.00}m across");
        }

        // ── looking at things ───────────────────────────────────────────

        private static string Frame(SceneView view, string json)
        {
            if (!Aim(view, json, out GameObject go, out Bounds b, out string err)) return err;
            view.orthographic = false;
            view.rotation = Quaternion.Euler(PlaytestJson.Num(json, "pitch", 20f),
                                             PlaytestJson.Num(json, "yaw", 35f), 0f);
            return Shoot(view, $"{go.name}, {Size(b)}", Cut(view, b));
        }

        /// <summary>A ring of angles, bundled into one picture.
        ///
        /// THE ONE THAT MATTERS. "Show me this object" and "show me this object
        /// unobstructed" are the same request, and no single angle answers it —
        /// whatever stands in front of the thing from here is exactly what you
        /// cannot see past, and you do not know it is there until you have moved.
        /// </summary>
        private static string Orbit(SceneView view, string json)
        {
            if (!Aim(view, json, out GameObject go, out Bounds b, out string err)) return err;

            int angles = Mathf.Clamp(Mathf.RoundToInt(PlaytestJson.Num(json, "angles", 4f)), 1, 8);
            float pitch = PlaytestJson.Num(json, "pitch", 20f);
            float from = PlaytestJson.Num(json, "yaw", 0f);
            view.orthographic = false;

            // KEEP THE CAMERA IN THE ROOM. Half a ring around a desk that stands
            // against a wall puts the camera outside the building, and clipping the
            // wall away leaves the shell rendering as a black slab — two wasted
            // cells out of four, which is what this returned on its first real use.
            // Pulling in until the eye is back inside the scene's own shell frames
            // tighter but always shows something.
            // Inset, because the scene's bounds INCLUDE the walls — an eye that is
            // merely inside them can still be buried in one, which is how an angle
            // came back as a flat rectangle of wallpaper.
            Bounds room = SceneBounds();
            room.Expand(-1.2f);
            float wanted = view.size;

            var frames = new List<Texture2D>();
            var yaws = new StringBuilder();
            int blank = 0, blocked = 0;
            for (int i = 0; i < angles; i++)
            {
                float yaw = from + i * (360f / angles);
                view.rotation = Quaternion.Euler(pitch, yaw, 0f);

                view.size = wanted;
                while (view.size > wanted * 0.5f && !room.Contains(Eye(view)))
                    view.size *= 0.8f;

                // Half as far back still shows the object; a quarter shows one
                // corner of it very large, which reads as an answer and is not one.
                // If it cannot get indoors without going closer than that, the angle
                // is not available and saying so beats faking it.
                if (!room.Contains(Eye(view))) { blocked++; continue; }

                var shot = Capture(view, Cut(view, b));

                // A cell of nothing costs as much as a cell of something. If the
                // angle came back empty anyway, say so in words instead.
                if (IsBlank(shot)) { Object.DestroyImmediate(shot); blank++; continue; }

                frames.Add(shot);
                yaws.Append(yaws.Length == 0 ? "" : ", ").Append($"{Mathf.Repeat(yaw, 360f):0}°");
            }
            view.size = wanted;

            if (frames.Count == 0)
                return Fail($"none of the {angles} angles worked — {go.name} is enclosed, with no " +
                            "room to stand back from it on any side. Try `plan`, which cuts a " +
                            "section instead of walking around, or `frame` with an explicit yaw.");

            string sheet = Write("orbit", PlaytestContactSheet.Compose(frames, CellWidth));
            foreach (var f in frames) Object.DestroyImmediate(f);

            return Publish(sheet, $"{go.name} FROM {frames.Count} ANGLE(S), numbered 1-{frames.Count}, " +
                                  $"left to right then down — yaw {yaws}, pitch {pitch:0}°. {Size(b)}. " +
                                  "Anything nearer than the object is cut away, so a wall behind you " +
                                  "never becomes the picture." +
                                  (blank + blocked > 0
                                      ? $" {blank + blocked} further angle(s) dropped: the object is " +
                                        "against something on that side, so there is no room to stand " +
                                        "back and look at it from there. Try `plan`, which cuts a " +
                                        "section instead of walking around."
                                      : ""));
        }

        /// <summary>Orthographic plan and elevations — the draughtsman's views.
        ///
        /// This is what "switch to 2D" should mean here. Unity's own 2D mode is for
        /// sprite work: it locks the camera looking down -Z, which in a room is a
        /// view of one wall. What you actually want when you ask for it is a view
        /// with NO PERSPECTIVE, because perspective is exactly what makes it
        /// impossible to tell whether the chair is touching the desk or a foot in
        /// front of it. Top answers that in one picture.</summary>
        private static string Plan(SceneView view, string json)
        {
            string from = (PlaytestJson.Str(json, "from") ?? "top").ToLowerInvariant();
            string what = "the scene";
            float reach = 0f;
            if (!string.IsNullOrEmpty(PlaytestJson.Str(json, "target")))
            {
                if (!Aim(view, json, out GameObject go, out Bounds b, out string err)) return err;
                what = go.name + ", " + Size(b);
                reach = b.extents.magnitude * 1.2f;
            }

            view.orthographic = true;
            switch (from)
            {
                case "front": view.rotation = Quaternion.Euler(0f, 0f, 0f); break;
                case "back": view.rotation = Quaternion.Euler(0f, 180f, 0f); break;
                case "side":
                case "left": view.rotation = Quaternion.Euler(0f, 90f, 0f); break;
                case "right": view.rotation = Quaternion.Euler(0f, 270f, 0f); break;
                case "iso": view.rotation = Quaternion.Euler(30f, 45f, 0f); break;
                default:
                    from = "top";
                    view.rotation = Quaternion.Euler(89.9f, 0f, 0f);   // 90 flat is gimbal-ambiguous
                    break;
            }
            float near = reach > 0f ? Mathf.Max(0.01f, Distance(view) - reach) : 0f;

            return Shoot(view, $"{what} — orthographic {from} view, so nothing is foreshortened " +
                               "and touching reads as touching" +
                               (near > 0.01f ? ", cut back to the target so nothing in front of it blocks the view" : ""),
                         near);
        }

        // ── shared ──────────────────────────────────────────────────────

        private static bool Aim(SceneView view, string json,
                                out GameObject go, out Bounds bounds, out string error)
        {
            go = null; bounds = default; error = null;
            string target = PlaytestJson.Str(json, "target");
            if (string.IsNullOrEmpty(target))
            {
                error = Fail("frame, orbit and plan need a \"target\" — an object name like " +
                             "\"Desk\", or a path like \"Room/Env/Desk\". Use \"look\", \"turn\" or " +
                             "\"pan\" to move about without naming anything.");
                return false;
            }

            go = Locate(target);
            if (go == null)
            {
                error = Fail($"found nothing matching '{target}'. It is tried as a full path, then " +
                             "an exact name, then any substring, including inactive objects — so a " +
                             "shorter fragment is more likely to hit, not less.");
                return false;
            }
            if (!TryBounds(go, out bounds))
            {
                error = Fail($"'{go.name}' has no renderers anywhere beneath it, so there is nothing " +
                             "to look at. It is probably a grouping object — try one of its children.");
                return false;
            }

            view.pivot = bounds.center;
            view.size = Mathf.Max(0.05f, bounds.extents.magnitude *
                                         Mathf.Max(0.2f, PlaytestJson.Num(json, "margin", 1.6f)));
            return true;
        }

        private static string Shoot(SceneView view, string what, float near = 0f)
        {
            var tex = Capture(view, near);
            string path = Write("view", tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return Publish(path, what + $" — looking at {view.pivot.ToString("0.00")} from " +
                                        $"{Eye(view).ToString("0.00")}.");
        }

        /// <summary>Render the Scene View camera at a FIXED size, so the result does
        /// not depend on how someone happens to have their editor laid out — and
        /// with gizmos off, because a capture full of icons and grid lines is a
        /// capture of the editor rather than of the scene.</summary>
        private static Texture2D Capture(SceneView view, float near = 0f)
        {
            const int W = 960, H = 540;

            // A CAMERA OF OUR OWN, rather than borrowing the Scene View's.
            //
            // Two reasons, both learned the hard way. The Scene View's camera keeps
            // a viewport rect and a cached HDRP resolution matched to the editor
            // WINDOW, so rendering it into a 960x540 target produced the picture at
            // the window's size in one corner and grey everywhere else. And
            // `view.pivot` / `view.rotation` are animated targets that the Scene
            // View eases toward, syncing its camera only when it repaints — so
            // rendering straight after setting them photographs the camera where it
            // used to be, and a top view comes back as the previous angle.
            //
            // A fresh camera has neither problem: it is posed, used, and destroyed.
            var holder = EditorUtility.CreateGameObjectWithHideFlags(
                "~InspectCamera", HideFlags.HideAndDontSave, typeof(Camera));
            var cam = holder.GetComponent<Camera>();

            cam.CopyFrom(view.camera);
            cam.rect = new Rect(0f, 0f, 1f, 1f);
            cam.aspect = W / (float)H;
            cam.transform.SetPositionAndRotation(Eye(view), view.rotation);
            cam.orthographic = view.orthographic;
            if (view.orthographic) cam.orthographicSize = view.size;
            cam.nearClipPlane = near > 0.01f ? near : 0.01f;
            cam.farClipPlane = Mathf.Max(1000f, Distance(view) * 4f);

            var rt = RenderTexture.GetTemporary(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            // HDRP will not reliably render a camera just because it has a target;
            // a render REQUEST is synchronous and ignores the per-frame loop.
            var request = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, request))
                RenderPipeline.SubmitRenderRequest(cam, request);
            else
                cam.Render();

            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            RenderTexture.active = prevActive;

            if (_brighten) Lift(tex);
            tex.Apply();

            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            Object.DestroyImmediate(holder);

            // Leave the human's Scene View looking where the agent looked, so the
            // two are never quietly out of step.
            view.Repaint();
            return tex;
        }

        /// <summary>Where the camera actually is. The Scene View stores a pivot and
        /// a field size, not a position, so this is derived rather than read.</summary>
        private static Vector3 Eye(SceneView view) =>
            view.pivot - view.rotation * Vector3.forward * Distance(view);

        /// <summary>How far back the camera sits to show a field `size` across.
        ///
        /// Derived rather than taken from `view.cameraDistance`, which returns a
        /// number in the 1e34 range under orthographic — mathematically true, since
        /// a parallel projection has no eye point, and useless for placing a camera
        /// or a clip plane. Orthographic just needs to be far enough back to clear
        /// the geometry.</summary>
        private static float Distance(SceneView view) =>
            view.orthographic
                ? view.size * 4f + 10f
                : view.size / Mathf.Sin(view.camera.fieldOfView * 0.5f * Mathf.Deg2Rad);

        private static float Wrap(float degrees) => degrees > 180f ? degrees - 360f : degrees;

        /// <summary>Stretch the picture so its brightest real content reaches near
        /// white.
        ///
        /// A MEASURING TOOL MUST NOT DEPEND ON THE ART BEING LIT. This room is a
        /// night scene lit by one oil lamp: a side elevation of a pale score against
        /// a warm wall reads, and the same shot of a dark object against a dark wall
        /// is a black rectangle that answers nothing. The geometry was always there;
        /// only the exposure was wrong.
        ///
        /// Done on the pixels rather than in the render, deliberately — every
        /// alternative (a headlight, an unlit replacement shader, a debug display
        /// mode) means knowing which render pipeline this package is installed in,
        /// and it is supposed to work in any of them. It only ever brightens, so a
        /// well-exposed shot passes through untouched.</summary>
        private static void Lift(Texture2D tex)
        {
            var px = tex.GetRawTextureData<byte>();

            // The brightest few pixels are usually a lamp or a specular hit, and
            // scaling to those leaves everything else as dark as it started. Aim at
            // the 98th percentile instead — the brightest real SURFACE.
            var histogram = new int[256];
            for (int i = 0; i < px.Length; i += 3)
                histogram[Mathf.Max(px[i], Mathf.Max(px[i + 1], px[i + 2]))]++;

            int total = px.Length / 3, seen = 0, top = 255;
            for (int v = 255; v >= 0; v--)
            {
                seen += histogram[v];
                if (seen > total * 0.02f) { top = v; break; }
            }
            if (top >= 235 || top < 4) return;   // already exposed, or nothing there

            float gain = 235f / top;
            for (int i = 0; i < px.Length; i++)
                px[i] = (byte)Mathf.Min(255f, px[i] * gain);
        }

        /// <summary>Did this angle come back with nothing IN it?
        ///
        /// The test is featurelessness, not darkness. The first version asked
        /// whether the frame was dark, and duly caught the black cells — then
        /// passed a flat pink rectangle, which was the camera pressed against a
        /// wall and just as useless. What makes a picture worth a cell is variation;
        /// a single flat colour is nothing whatever its brightness.
        ///
        /// Judged BEFORE the lift, or an empty frame gets brightened into
        /// convincing noise and buys itself a cell.</summary>
        private static bool IsBlank(Texture2D tex)
        {
            var px = tex.GetRawTextureData<byte>();

            long sum = 0;
            int looked = 0;
            for (int i = 0; i < px.Length; i += 33) { sum += px[i]; looked++; }
            float mean = sum / (float)looked;

            int varied = 0;
            for (int i = 0; i < px.Length; i += 33)
                if (Mathf.Abs(px[i] - mean) > 12f) varied++;

            return varied < looked * 0.05f;
        }

        /// <summary>The whole scene's extent — for an interior, the building shell.
        /// Used to keep an orbiting camera indoors.</summary>
        private static Bounds SceneBounds()
        {
            Bounds all = default;
            bool any = false;
            foreach (var r in Object.FindObjectsByType<Renderer>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!any) { all = r.bounds; any = true; }
                else all.Encapsulate(r.bounds);
            }
            return any ? all : new Bounds(Vector3.zero, Vector3.one * 100f);
        }

        /// <summary>Where to put the near clip plane so nothing in front of the
        /// subject survives — a section cut, the way a plan drawing makes one.
        ///
        /// THIS IS WHAT MAKES INSPECTION WORK INDOORS. A top view of a room taken
        /// from above it is a photograph of the ceiling. An orbit around a desk that
        /// stands against a wall spends half its ring inside that wall, and returns
        /// two good angles and two rectangles of black. Both were real results here
        /// before this existed. Clipping everything nearer than the subject deletes
        /// whatever stands between you and it, from any direction, without touching
        /// the scene or moving a single object.</summary>
        /// The margin is just over 1: the subject's bounding SPHERE already reaches
        /// further than the subject does, so 1.05 clears it completely while cutting
        /// as much of the room in front of it as possible. 1.2 left a wall standing
        /// in one angle out of four.
        private static float Cut(SceneView view, Bounds b) =>
            Mathf.Max(0.01f, Distance(view) - b.extents.magnitude * 1.05f);

        private static string Size(Bounds b) =>
            $"{b.size.x:0.00} x {b.size.y:0.00} x {b.size.z:0.00}m, centred {b.center.ToString("0.00")}";

        /// <summary>Full path, then exact name, then substring — including inactive
        /// objects, which GameObject.Find silently skips.
        ///
        /// Forgiving on purpose. You reach for a developer camera exactly when you
        /// do NOT know where something is, so a lookup that demands the answer you
        /// came to find is no use.</summary>
        private static GameObject Locate(string query)
        {
            var exact = GameObject.Find(query);
            if (exact != null) return exact;

            GameObject partial = null;
            foreach (var t in Object.FindObjectsByType<Transform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t.name.Equals(query, System.StringComparison.OrdinalIgnoreCase)) return t.gameObject;
                if (partial == null && t.name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    partial = t.gameObject;
            }
            return partial;
        }

        /// <summary>Bounds over renderers, not the transform. A transform is a
        /// point, and framing a point tells you nothing about how big a thing is —
        /// which is most of what you came to find out.</summary>
        private static bool TryBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            var renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        private static string Write(string label, byte[] png)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playtest");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{++_seq:000}_{label}.png");
            System.IO.File.WriteAllBytes(path, png);
            return path;
        }

        /// <summary>Same wire shape as a playtest action, so the MCP server hands
        /// pictures back inline without knowing which tool produced them.</summary>
        private static string Publish(string path, string caption) =>
            "{\"ok\": true, \"did\": \"inspected\", \"screen\": [960, 540], \"shots\": [" +
            $"{{\"path\": \"{PlaytestJson.Escape(path.Replace('\\', '/'))}\", " +
            $"\"caption\": \"{PlaytestJson.Escape(caption)}\"}}]}}";

        private static string Fail(string why) =>
            $"{{\"ok\": false, \"error\": \"{PlaytestJson.Escape(why)}\"}}";
    }
}
