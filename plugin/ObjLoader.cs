using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using UnityEngine;

namespace BoatMod
{
    public class MeshGroup
    {
        public string MatName;
        public Color Color = new Color(0.8f, 0.8f, 0.8f, 1f);
        public float Metallic;
        public float Roughness = 0.85f;
        public List<Vector3> Verts = new List<Vector3>();
        public List<Vector3> Norms = new List<Vector3>();
        public List<int> Tris = new List<int>();

        public Mesh ToMesh()
        {
            var m = new Mesh
            {
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                vertices = Verts.ToArray(),
                normals = Norms.ToArray(),
                triangles = Tris.ToArray()
            };
            if (m.normals.Length != m.vertexCount) m.normals = null;
            m.RecalculateBounds();
            return m;
        }
    }

    public static class ObjLoader
    {
        public static List<MeshGroup> Load(string objPath, string mtlPath, ManualLogSource log)
        {
            var colors = ParseMtl(mtlPath, log);
            var groups = new Dictionary<string, MeshGroup>();
            MeshGroup current = null;

            var gV = new List<Vector3>();
            var gN = new List<Vector3>();

            foreach (var raw in File.ReadAllLines(objPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var sp = line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
                switch (sp[0])
                {
                    case "v":
                        gV.Add(new Vector3(P(sp[1]), P(sp[2]), P(sp[3])));
                        break;
                    case "vn":
                        gN.Add(new Vector3(P(sp[1]), P(sp[2]), P(sp[3])));
                        break;
                    case "usemtl":
                        var mn = line.Substring(6).Trim();
                        if (!groups.TryGetValue(mn, out var g))
                        {
                            g = new MeshGroup { MatName = mn };
                            if (colors.TryGetValue(mn, out var mc))
                            {
                                g.Color = mc.c;
                                g.Metallic = mc.met;
                                g.Roughness = mc.rgh;
                            }
                            groups[mn] = g;
                        }
                        current = g;
                        break;
                    case "f":
                        if (current == null)
                        {
                            current = new MeshGroup { MatName = "__default" };
                            groups["__default"] = current;
                        }
                        AddFace(current, sp, gV, gN);
                        break;
                }
            }

            var list = new List<MeshGroup>(groups.Values);
            int vsum = 0, tsum = 0;
            foreach (var g in list) { vsum += g.Verts.Count; tsum += g.Tris.Count / 3; }
            log.LogInfo($"[ObjLoader] {list.Count} material groups, {vsum} verts, {tsum} tris");
            return list;
        }

        static float P(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        static void AddFace(MeshGroup g, string[] sp, List<Vector3> gV, List<Vector3> gN)
        {
            var corners = new (int v, int n)[sp.Length - 1];
            for (int i = 1; i < sp.Length; i++)
            {
                var parts = sp[i].Split('/');
                int vi = Resolve(parts[0], gV.Count);
                int ni = parts.Length >= 3 && parts[2].Length > 0 ? Resolve(parts[2], gN.Count) : -1;
                corners[i - 1] = (vi, ni);
            }
            for (int i = 1; i < corners.Length - 1; i++)
            {
                int baseIdx = g.Verts.Count;
                foreach (var c in new[] { corners[0], corners[i], corners[i + 1] })
                {
                    g.Verts.Add(gV[c.v]);
                    g.Norms.Add(c.n >= 0 ? gN[c.n] : Vector3.up);
                    g.Tris.Add(baseIdx++);
                }
            }
        }

        static int Resolve(string tok, int count)
        {
            int i = int.Parse(tok, CultureInfo.InvariantCulture);
            return i > 0 ? i - 1 : count + i;
        }

        static Dictionary<string, (Color c, float met, float rgh)> ParseMtl(string path, ManualLogSource log)
        {
            var map = new Dictionary<string, (Color, float, float)>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                log.LogWarning("[ObjLoader] no .mtl found, using default gray");
                return map;
            }
            string cur = null;
            var col = new Color(0.8f, 0.8f, 0.8f, 1f);
            float met = 0f, rgh = 0.85f;
            void Flush() { if (cur != null) map[cur] = (col, met, rgh); }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("# metallic"))
                {
                    if (float.TryParse(line.Substring(11).Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) met = v;
                }
                else if (line.StartsWith("# roughness"))
                {
                    if (float.TryParse(line.Substring(12).Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) rgh = v;
                }
                else
                {
                    var sp = line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);
                    if (sp.Length < 2) continue;
                    if (sp[0] == "newmtl")
                    {
                        Flush();
                        cur = raw.Trim().Substring(6).Trim();
                        col = new Color(0.8f, 0.8f, 0.8f, 1f);
                        met = 0f; rgh = 0.85f;
                    }
                    else if (sp[0] == "Kd" && sp.Length >= 4)
                        col = new Color(P(sp[1]), P(sp[2]), P(sp[3]), 1f);
                }
            }
            Flush();
            log.LogInfo($"[ObjLoader] parsed {map.Count} materials from mtl");
            return map;
        }
    }
}
