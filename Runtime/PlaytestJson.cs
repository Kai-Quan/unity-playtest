#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using UnityEngine;

namespace KaiQuan.Playtest
{
    /// <summary>
    /// The smallest JSON reader that does this job: flat objects of strings,
    /// numbers and [x,y] pairs.
    ///
    /// Hand-rolled because pulling a parser into a package to read
    /// {"action":"orbit","target":"Organ"} would be the larger cost. Extracted from
    /// PlaytestAction when SceneInspector needed the same three functions — a
    /// duplicated parser is how a small tool becomes an unmaintainable one.
    /// </summary>
    public static class PlaytestJson
    {
        public static string Str(string json, string key)
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

        public static float Num(string json, string key, float fallback)
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

        public static Vector2? Pair(string json, string key)
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

        public static string Escape(string s) =>
            string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                                            .Replace("\n", " ").Replace("\r", "");
    }
}
#endif
