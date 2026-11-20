// Z-파이팅(같은 평면에 겹친 서로 다른 재질의 면이 프레임마다 번갈아 보이며 깜빡이는 현상) 자동 해소.
// 규칙: 한 덩어리(모델 또는 Batch) 안에서 재질을 면적 순으로 정렬하고, 작은 재질(라벨, LED, 손잡이, 표시선 등 디테일)일수록
//       면 법선 방향으로 조금씩 더 바깥으로 밀어냅니다. 겹친 면은 항상 디테일 쪽이 앞에 보이게 됩니다.
// 밀어내는 양은 순위당 0.8 mm (최대 4.8 mm) 로, 화면에서 형상 차이는 보이지 않습니다.
using System.Collections.Generic;
using UnityEngine;

namespace EquipFX
{
    public static class MeshFix
    {
        public const float Step = 0.0008f;
        public const int MaxRank = 6;
        static readonly Dictionary<(Mesh, int), Mesh> cache = new Dictionary<(Mesh, int), Mesh>();
        static bool warned;

        public static float OffsetForRank(int rank) => Step * Mathf.Min(rank, MaxRank);

        // 이미 만들어진 메쉬 전체를 법선 방향으로 d 만큼 부풀림 (Batch 결과 메쉬용, 메쉬를 직접 수정)
        public static void Inflate(Mesh mesh, float d)
        {
            if (d <= 0 || mesh == null) return;
            var v = mesh.vertices; var n = mesh.normals;
            if (n == null || n.Length != v.Length) return;
            for (int i = 0; i < v.Length; i++) v[i] += n[i] * d;
            mesh.vertices = v; mesh.RecalculateBounds();
        }

        // Batch 처럼 재질마다 하나로 합친 메쉬들: 넓은 재질부터 순위를 매겨 작은 재질일수록 바깥으로 밀어냄
        public static void InflateByArea(IList<KeyValuePair<Material, Mesh>> parts)
        {
            var area = new Dictionary<Material, float>();
            foreach (var kv in parts)
            {
                float a = 0; for (int s = 0; s < kv.Value.subMeshCount; s++) a += Area(kv.Value, s, Vector3.one);
                area.TryGetValue(kv.Key, out var a0); area[kv.Key] = a0 + a;
            }
            var rank = RankByArea(area);
            foreach (var kv in parts) Inflate(kv.Value, OffsetForRank(rank[kv.Key]));
        }

        // 재질별 면적 → 순위 (0 = 가장 넓은 재질, 밀지 않음)
        public static Dictionary<Material, int> RankByArea(Dictionary<Material, float> area)
        {
            var list = new List<KeyValuePair<Material, float>>(area);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var r = new Dictionary<Material, int>();
            for (int i = 0; i < list.Count; i++) r[list[i].Key] = i;
            return r;
        }

        public static float Area(Mesh m, int sub, Vector3 scale)
        {
            var v = m.vertices; var t = m.GetTriangles(sub); float a = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                var p0 = Vector3.Scale(v[t[i]], scale); var p1 = Vector3.Scale(v[t[i + 1]], scale); var p2 = Vector3.Scale(v[t[i + 2]], scale);
                a += Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            }
            return a;
        }

        // FBX 모델 인스턴스: 재질 면적 순위대로 서브메쉬를 밀어낸 메쉬로 교체. 같은 원본 메쉬는 한 번만 처리해 공유.
        public static void ResolveModel(GameObject go)
        {
            var mfs = go.GetComponentsInChildren<MeshFilter>(true);
            var area = new Dictionary<Material, float>();
            foreach (var mf in mfs)
            {
                var m = mf.sharedMesh; var r = mf.GetComponent<Renderer>();
                if (m == null || r == null) continue;
                if (!m.isReadable) { if (!warned) { Debug.LogWarning("[EquipFX] 메쉬를 읽을 수 없어 겹친 면 보정을 건너뜁니다: " + m.name + " (모델 Read/Write 를 켜세요)"); warned = true; } return; }
                var mats = r.sharedMaterials; var sc = mf.transform.lossyScale;
                for (int s = 0; s < m.subMeshCount && s < mats.Length; s++)
                {
                    if (mats[s] == null) continue;
                    area.TryGetValue(mats[s], out var a); area[mats[s]] = a + Area(m, s, sc);
                }
            }
            if (area.Count < 2) return;
            var rank = RankByArea(area);
            foreach (var mf in mfs)
            {
                var m = mf.sharedMesh; var r = mf.GetComponent<Renderer>();
                if (m == null || r == null) continue;
                var mats = r.sharedMaterials;
                // 이 메쉬에 쓰인 순위 조합을 키로 캐시
                int key = 17;
                for (int s = 0; s < m.subMeshCount && s < mats.Length; s++) key = key * 31 + (mats[s] != null && rank.TryGetValue(mats[s], out var rk) ? rk : 0);
                float scl = Mathf.Max(1e-6f, (Mathf.Abs(mf.transform.lossyScale.x) + Mathf.Abs(mf.transform.lossyScale.y) + Mathf.Abs(mf.transform.lossyScale.z)) / 3f);
                key = key * 31 + Mathf.RoundToInt(scl * 1000);
                if (!cache.TryGetValue((m, key), out var fixedMesh))
                {
                    fixedMesh = Split(m, s => (s < mats.Length && mats[s] != null && rank.TryGetValue(mats[s], out var rk2) ? OffsetForRank(rk2) : 0) / scl);
                    cache[(m, key)] = fixedMesh;
                }
                mf.sharedMesh = fixedMesh;
            }
        }

        // 서브메쉬마다 정점을 따로 복사(다른 재질과 공유하는 정점이 같이 움직이지 않게)하고 법선 방향으로 밀어냄
        static Mesh Split(Mesh src, System.Func<int, float> offset)
        {
            var v = src.vertices; var n = src.normals; var t4 = src.tangents;
            var uv = new List<Vector2>(); src.GetUVs(0, uv);
            bool hasN = n != null && n.Length == v.Length, hasT = t4 != null && t4.Length == v.Length, hasUV = uv.Count == v.Length;
            var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nt = new List<Vector4>(); var nuv = new List<Vector2>();
            var subs = new List<int[]>();
            for (int s = 0; s < src.subMeshCount; s++)
            {
                float d = offset(s);
                var tri = src.GetTriangles(s);
                var map = new Dictionary<int, int>();
                var nt2 = new int[tri.Length];
                for (int i = 0; i < tri.Length; i++)
                {
                    int o = tri[i];
                    if (!map.TryGetValue(o, out int ni))
                    {
                        ni = nv.Count; map[o] = ni;
                        nv.Add(hasN ? v[o] + n[o] * d : v[o]);
                        if (hasN) nn.Add(n[o]);
                        if (hasT) nt.Add(t4[o]);
                        if (hasUV) nuv.Add(uv[o]);
                    }
                    nt2[i] = ni;
                }
                subs.Add(nt2);
            }
            var m = new Mesh { name = src.name + "_zfix", indexFormat = nv.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(nv);
            if (hasN) m.SetNormals(nn);
            if (hasT) m.SetTangents(nt);
            if (hasUV) m.SetUVs(0, nuv);
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s, false);
            m.RecalculateBounds();
            if (!hasN) m.RecalculateNormals();
            return m;
        }
    }
}
