// 모든 설비 장면이 같이 쓰는 컷 기반 카메라 연출.
// 30초를 여러 "샷"으로 나누고, 샷마다 (1) 고정 경로 이동 또는 (2) 움직이는 설비를 따라가는 추적 샷을 씁니다.
// 샷 사이는 하드 컷이며, 컷 프레임에는 모션 블러를 잠깐 끕니다 (이전 프레임과의 카메라 차이로 화면 전체가 번지는 것 방지).
// 각 샷의 title/detail 은 CaptionOverlay 가 화면 아래 자막으로 보여 줍니다.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EquipFX
{
    public struct CamPose { public Vector3 pos, look; public float lens; }

    public abstract class Shot
    {
        public float start, end;          // 초 (30초 영상 기준)
        public string title, detail;      // 자막 (비우면 자막 없음)
        public float Duration => Mathf.Max(1e-3f, end - start);
        public Action onBegin;            // 샷이 시작될 때 (추적할 대상 고르기 등)
        public virtual void Begin() { }
        // u: 샷 안 진행률 0..1, dt: 프레임 간격
        public abstract CamPose Eval(float u, float dt);

        public static float Ease(float u) => u * u * (3f - 2f * u);
    }

    // 키 몇 개를 잇는 경로 샷 (Catmull-Rom, 축마다 두 키 사이로 제한해 곡선이 구조물 쪽으로 튀지 않게)
    public class PathShot : Shot
    {
        public struct Key { public float u; public Vector3 pos, look; public float lens; }
        readonly List<Key> keys = new List<Key>();
        public bool easeEnds = false;
        // 바라보는 점을 움직이는 대상에 일부 고정 (0 = 경로 그대로, 1 = 대상만 봄)
        public Func<Vector3> lookTarget; public float lookLock;
        Vector3 lookVel, lookSm; bool lookInit;

        public PathShot Add(float u, Vector3 pos, Vector3 look, float lens) { keys.Add(new Key { u = u, pos = pos, look = look, lens = lens }); return this; }

        public override void Begin() { lookInit = false; }

        static Vector3 CR(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
        static Vector3 Clamp(Vector3 v, Vector3 a, Vector3 b) =>
            new Vector3(Mathf.Clamp(v.x, Mathf.Min(a.x, b.x), Mathf.Max(a.x, b.x)),
                        Mathf.Clamp(v.y, Mathf.Min(a.y, b.y), Mathf.Max(a.y, b.y)),
                        Mathf.Clamp(v.z, Mathf.Min(a.z, b.z), Mathf.Max(a.z, b.z)));

        public override CamPose Eval(float u, float dt)
        {
            if (easeEnds) u = Ease(u);
            if (keys.Count == 1) return Finish(new CamPose { pos = keys[0].pos, look = keys[0].look, lens = keys[0].lens }, dt);
            int i = 0; while (i < keys.Count - 2 && u > keys[i + 1].u) i++;
            var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(keys.Count - 1, i + 2)];
            float s = Mathf.Clamp01(Mathf.InverseLerp(k1.u, k2.u, u));
            var p = new CamPose
            {
                pos = Clamp(CR(k0.pos, k1.pos, k2.pos, k3.pos, s), k1.pos, k2.pos),
                look = Clamp(CR(k0.look, k1.look, k2.look, k3.look, s), k1.look, k2.look),
                lens = Mathf.Lerp(k1.lens, k2.lens, Ease(s)),
            };
            return Finish(p, dt);
        }

        CamPose Finish(CamPose p, float dt)
        {
            if (lookTarget == null || lookLock <= 0) return p;
            var tgt = lookTarget();
            if (!lookInit) { lookSm = tgt; lookVel = Vector3.zero; lookInit = true; }
            else lookSm = Vector3.SmoothDamp(lookSm, tgt, ref lookVel, 0.18f, Mathf.Infinity, Mathf.Max(dt, 1e-4f));
            p.look = Vector3.Lerp(p.look, lookSm, lookLock);
            return p;
        }
    }

    // 움직이는 대상(로봇, 박스 등)을 따라가는 추적 샷.
    // 카메라 = 부드럽게 따라간 대상 위치 + 오프셋(샷 동안 offset0 → offset1 로 천천히 돌거나 다가감)
    public class FollowShot : Shot
    {
        public Func<Vector3> target;
        public Vector3 offset0, offset1;      // 월드 기준 오프셋 (Unity 좌표, y = 위)
        public Vector3 lookOffset;            // 바라보는 점 = 대상 + lookOffset
        public float lens0 = 35, lens1 = 35;
        public float smooth = 0.22f;          // 위치 추종 지연 (초). 작을수록 딱 붙음
        // 카메라가 들어가면 안 되는 영역 밖으로 밀어내는 함수 (선택)
        public Func<Vector3, Vector3> constrain;
        Vector3 sm, vel; bool init;

        public override void Begin() { init = false; }

        public override CamPose Eval(float u, float dt)
        {
            var tgt = target();
            if (!init) { sm = tgt; vel = Vector3.zero; init = true; }
            else sm = Vector3.SmoothDamp(sm, tgt, ref vel, smooth, Mathf.Infinity, Mathf.Max(dt, 1e-4f));
            float e = Ease(u);
            var pos = sm + Vector3.Lerp(offset0, offset1, e);
            if (constrain != null) pos = constrain(pos);
            return new CamPose { pos = pos, look = sm + lookOffset, lens = Mathf.Lerp(lens0, lens1, e) };
        }
    }

    public class ShotSequencer
    {
        public readonly List<Shot> shots = new List<Shot>();
        public float Duration => shots.Count > 0 ? shots[shots.Count - 1].end : 30f;
        public Shot Current { get; private set; }
        public float CurrentU { get; private set; }
        int cur = -1;

        public T Add<T>(T s) where T : Shot { shots.Add(s); return s; }

        public void Reset() { cur = -1; Current = null; }

        // t: 영상 시작 후 초. cut = 이번 프레임에 새 샷이 시작됨
        public CamPose Evaluate(float t, float dt, out bool cut)
        {
            cut = false;
            int i = 0; while (i < shots.Count - 1 && t >= shots[i].end) i++;
            var s = shots[i];
            if (i != cur) { cut = true; cur = i; s.onBegin?.Invoke(); s.Begin(); }
            Current = s;
            CurrentU = Mathf.Clamp01((t - s.start) / s.Duration);
            return s.Eval(CurrentU, dt);
        }

        public Shot At(float t)
        {
            foreach (var s in shots) if (t >= s.start && t < s.end) return s;
            return shots.Count > 0 ? shots[shots.Count - 1] : null;
        }
    }

    // 카메라 깊이 정밀도 / 그림자 / 모션 블러 등 깜빡임 관련 공통 설정
    public static class CameraFX
    {
        // near 를 너무 작게 잡으면 (0.02 m) 원거리의 얇은 표시선·라벨이 깊이 정밀도 부족으로 깜빡입니다.
        // 카메라가 구조물에 0.2 m 이내로 붙는 샷이 없으므로 0.1 m 로 올리고 far 도 장면 크기에 맞춥니다.
        public static void ConfigureClip(Camera cam, float near = 0.1f, float far = 120f)
        {
            cam.nearClipPlane = near; cam.farClipPlane = far;
        }

        // 녹화용 RenderTexture: 32비트 부동소수 깊이 (리버스 Z 와 함께 원거리 정밀도가 크게 좋아짐)
        // msaa > 1 이면 카메라가 이 텍스처에 MSAA 로 그립니다 (RecordSession 이 사용).
        public static RenderTexture MakeRecordTarget(int w, int h, int msaa = 1)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0)
            {
                sRGB = true, msaaSamples = Mathf.Max(1, msaa),
                depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.D32_SFloat_S8_UInt,
            };
            var rt = new RenderTexture(desc); rt.Create();
            return rt;
        }
    }
}
