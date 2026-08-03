using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Elegist.Playtest.EditorTools
{
    /// <summary>
    /// The scene as NUMBERS — the twin of `inspect`, which is the scene as pictures.
    ///
    /// WHY THIS EXISTS. A scene that a person edits by hand and an agent also works
    /// on has one hard problem: neither can see what the other did. The agent's
    /// answer used to be a generator — one script holding a table of every
    /// coordinate, re-asserted on every run — which works right up until a human
    /// drags something, and then silently throws their work away. That happened
    /// here, twice, and the second time it cost a whole hand-authored layout.
    ///
    /// So: the SCENE is the source of truth, and this writes it down. A snapshot is
    /// a record, never an instruction. `write` puts the current layout in a file you
    /// commit; `diff` says what has moved since. The agent reads coordinates without
    /// asking, a review shows exactly what changed, and a fresh clone has something
    /// to rebuild from — all without anything ever overwriting a transform.
    ///
    ///     {"tool":"snapshot","root":"ExpeditionProto","out":"workbench/x/staging.json"}
    ///     {"tool":"snapshot","action":"diff","root":"ExpeditionProto","out":"..."}
    ///
    /// REFUSES DURING PLAY MODE, unlike `inspect`, and for the opposite reason.
    /// Inspect answers "what does this look like right now", and runtime state is a
    /// legitimate answer. A snapshot answers "what is this room", and in play mode
    /// every transform is whatever the game has done to it since — doors open,
    /// items in hand, the camera moved. Writing that over the authored layout would
    /// be indistinguishable from a correct snapshot and impossible to notice.
    /// </summary>
    public static class SceneSnapshot
    {
        public static string Run(string json)
        {
            if (Application.isPlaying)
                return Fail("play mode: every transform is runtime state, not the authored layout. " +
                            "Stop the game first — a snapshot taken now would look correct and be wrong.");

            string rootName = PlaytestJson.Str(json, "root");
            if (string.IsNullOrEmpty(rootName))
                return Fail("needs a root — the object whose subtree to record, e.g. \"ExpeditionProto\".");

            var root = GameObject.Find(rootName);
            if (root == null) return Fail($"no object called \"{rootName}\" in the open scene.");

            string outPath = PlaytestJson.Str(json, "out");
            if (string.IsNullOrEmpty(outPath))
                return Fail("needs an out path — where to write or compare against.");
            // Unity's working directory is the PROJECT folder, so a relative path
            // lands inside it and a workbench alongside the project is "../".
            outPath = Path.GetFullPath(outPath);

            var now = new Dictionary<string, string>();
            Walk(root.transform, root.transform, now);

            return PlaytestJson.Str(json, "action") == "diff"
                ? Diff(now, outPath)
                : WriteFile(now, outPath, root.name);
        }

        // ── write ───────────────────────────────────────────────────────

        private static string WriteFile(Dictionary<string, string> rows, string outPath, string root)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("{");
            int i = 0;
            foreach (var kv in rows)
                sb.AppendLine($"  \"{kv.Key}\": {kv.Value}{(++i < rows.Count ? "," : "")}");
            sb.AppendLine("}");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString());
            return Say($"wrote {rows.Count} objects under {root} to {outPath.Replace('\\', '/')}");
        }

        // ── diff ────────────────────────────────────────────────────────

        /// <summary>What has changed since the file was written. Reports only the
        /// differences, because the whole point is to answer "what did they move"
        /// without re-reading two hundred lines of coordinates.</summary>
        private static string Diff(Dictionary<string, string> now, string outPath)
        {
            if (!File.Exists(outPath))
                return Fail($"nothing to compare against — {outPath.Replace('\\', '/')} does not exist yet. " +
                            "Take a snapshot first.");

            var was = Parse(File.ReadAllText(outPath));
            var moved = new List<string>();
            var added = new List<string>();
            var gone = new List<string>();

            foreach (var kv in now)
            {
                if (!was.TryGetValue(kv.Key, out string old)) added.Add(kv.Key);
                else if (old != kv.Value) moved.Add($"{kv.Key}\n      was {old}\n      now {kv.Value}");
            }
            foreach (var kv in was)
                if (!now.ContainsKey(kv.Key)) gone.Add(kv.Key);

            if (moved.Count == 0 && added.Count == 0 && gone.Count == 0)
                return Say("no change — the scene matches the snapshot exactly.");

            var sb = new System.Text.StringBuilder();
            sb.Append($"{moved.Count} moved, {added.Count} new, {gone.Count} deleted.");
            foreach (var m in moved) sb.Append($"\n  ~ {m}");
            foreach (var a in added) sb.Append($"\n  + {a}");
            foreach (var g in gone) sb.Append($"\n  - {g}");
            return Say(sb.ToString());
        }

        /// <summary>Read the flat one-object-per-line format back. Deliberately not
        /// a general JSON parser: this only ever reads a file this class wrote, and
        /// a parser that accepts more than it produces is a parser with bugs in the
        /// part nobody exercises.</summary>
        private static Dictionary<string, string> Parse(string text)
        {
            var map = new Dictionary<string, string>();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim().TrimEnd(',');
                if (!line.StartsWith("\"")) continue;
                int close = line.IndexOf("\":", System.StringComparison.Ordinal);
                if (close < 1) continue;
                map[line.Substring(1, close - 1)] = line.Substring(close + 2).Trim();
            }
            return map;
        }

        // ── reading the scene ───────────────────────────────────────────

        private static void Walk(Transform root, Transform t, Dictionary<string, string> rows)
        {
            foreach (Transform c in t)
            {
                // An installed mesh is not authored — it is rebuilt from its .glb,
                // and its inner transforms are the installer's arithmetic. Record
                // WHICH file the object wears and skip the machinery.
                if (c.name == "Mesh_Generated") { RecordMesh(root, t, c, rows); continue; }

                rows[PathOf(root, c)] = Row(c);
                Walk(root, c, rows);
            }
        }

        private static void RecordMesh(Transform root, Transform owner, Transform holder,
                                       Dictionary<string, string> rows)
        {
            if (holder.childCount == 0) return;
            var inst = holder.GetChild(0);
            var src = PrefabUtility.GetCorrespondingObjectFromSource(inst.gameObject);
            string glb = src != null ? Path.GetFileName(AssetDatabase.GetAssetPath(src)) : inst.name;
            rows[PathOf(root, owner) + "#mesh"] =
                $"{{ \"glb\": \"{glb}\", \"yaw\": {F(inst.localEulerAngles.y)} }}";
        }

        private static string Row(Transform c)
        {
            var r = c.GetComponent<Renderer>();
            // Renderer state is here because it is layout in disguise: "hidden" and
            // "casts shadows only" are how a stand-in is retired and how an
            // invisible shadow proxy works, and both are invisible in a transform.
            string extra = r == null ? ""
                : $", \"shown\": {Bool(r.enabled)}, \"shadow\": \"{r.shadowCastingMode}\"";
            return $"{{ \"pos\": [{V(c.localPosition)}], \"rot\": [{V(c.localEulerAngles)}], " +
                   $"\"scale\": [{V(c.localScale)}], \"on\": {Bool(c.gameObject.activeSelf)}{extra} }}";
        }

        private static string PathOf(Transform root, Transform t)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts);
        }

        private static string V(Vector3 v) => $"{F(v.x)}, {F(v.y)}, {F(v.z)}";

        /// <summary>Four decimals, and a hard zero for anything under half a tenth
        /// of a millimetre. Without the snap, float noise makes every object look
        /// like it moved and a diff becomes unreadable.</summary>
        private static string F(float f) =>
            (Mathf.Abs(f) < 0.00005f ? 0f : f).ToString("0.####", CultureInfo.InvariantCulture);

        private static string Bool(bool b) => b ? "true" : "false";

        private static string Say(string what) =>
            $"{{\"ok\": true, \"did\": \"{PlaytestJson.Escape(what)}\", \"screen\": [0, 0], \"shots\": []}}";

        private static string Fail(string why) =>
            $"{{\"ok\": false, \"error\": \"{PlaytestJson.Escape(why)}\"}}";
    }
}
