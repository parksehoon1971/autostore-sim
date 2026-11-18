// 30초 시네마틱 카메라 (Blender 스크립트와 같은 동선) + 수동 오비트 카메라
using UnityEngine;
using UnityEngine.Rendering.Universal;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace AutoStore
{
    public class CinematicCamera : MonoBehaviour
    {
        public AutoStoreView view;
        public Transform target;
        public float duration = 30f;
        public bool loop = true;
        [HideInInspector] public DepthOfField dof;

        float t0 = -1;
        // 오비트 상태
        float yaw = 35, pitch = 25, dist = 14;
        Vector3 orbitCenter;

        struct Key { public float f; public Vector3 pos, look; public float lens; }
        Key[] keys;

        // Blender 좌표 (x, y, z-up) → Unity (x, z, y)
        static Vector3 B(float x, float y, float z) => new Vector3(x, z, y);

        void BuildKeys()
        {
            var c = view.config;
            float cx = c.nx * c.cellX / 2, cy = c.ny * c.cellY / 2, T = (float)view.Sim.topZ;
            float portX = c.ports[c.ports.Length / 2] * c.cellX + 0.35f;
            keys = new[]
            {
                new Key { f = 0.00f, pos = B(cx + 10.5f, cy - 11f, T + 3f),   look = B(cx, cy, T * 0.45f), lens = 24 },
                new Key { f = 0.22f, pos = B(cx + 4f, -5.2f, 1.9f),           look = B(portX, -0.4f, 1.1f), lens = 32 },
                new Key { f = 0.34f, pos = B(cx + 1f, -6.5f, T * 0.75f),      look = B(cx, cy * 0.5f, T * 0.8f), lens = 26 },
                new Key { f = 0.48f, pos = B(cx - 2.5f, -3.2f, T + 1.6f),     look = B(cx - 0.5f, cy * 0.7f, T + 0.2f), lens = 26 },
                new Key { f = 0.70f, pos = B(cx + 2.5f, cy + 0.5f, T + 2.6f), look = B(cx - 1f, cy + 0.3f, T), lens = 22 },
                new Key { f = 1.00f, pos = B(cx - 9.5f, cy + 9f, T + 4f),     look = B(cx, cy, T * 0.5f), lens = 24 },
            };
            orbitCenter = B(cx, cy, T * 0.5f);
        }

        public void Restart() { t0 = Time.time; }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        void LateUpdate()
        {
            if (view == null || view.Sim == null) return;
            if (keys == null) BuildKeys();
            var cam = GetComponent<Camera>();
            if (view.cinematicCamera)
            {
                if (t0 < 0) t0 = Time.time;
                float u = (Time.time - t0) / duration;
                if (loop) u = Mathf.Repeat(u, 1f); else u = Mathf.Clamp01(u);
                int i = 0; while (i < keys.Length - 2 && u > keys[i + 1].f) i++;
                var k0 = keys[Mathf.Max(0, i - 1)]; var k1 = keys[i]; var k2 = keys[i + 1]; var k3 = keys[Mathf.Min(keys.Length - 1, i + 2)];
                float s = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(k1.f, k2.f, u));
                transform.position = CatmullRom(k0.pos, k1.pos, k2.pos, k3.pos, s);
                var look = CatmullRom(k0.look, k1.look, k2.look, k3.look, s);
                transform.rotation = Quaternion.LookRotation(look - transform.position, Vector3.up);
                cam.focalLength = Mathf.Lerp(k1.lens, k2.lens, s);
                if (target) target.position = look;
            }
            else Orbit();
            if (dof != null && target) dof.focusDistance.value = Vector3.Distance(transform.position, target.position);
        }

        void Orbit()
        {
            Vector2 delta = Vector2.zero; float scroll = 0; bool drag = false;
#if ENABLE_INPUT_SYSTEM
            var m = Mouse.current;
            if (m != null) { drag = m.rightButton.isPressed || m.leftButton.isPressed; delta = m.delta.ReadValue() * 0.2f; scroll = m.scroll.ReadValue().y * 0.01f; }
#elif ENABLE_LEGACY_INPUT_MANAGER
            drag = Input.GetMouseButton(0) || Input.GetMouseButton(1);
            delta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * 3f; scroll = Input.mouseScrollDelta.y * 0.5f;
#endif
            if (drag) { yaw += delta.x; pitch = Mathf.Clamp(pitch - delta.y, 5, 85); }
            dist = Mathf.Clamp(dist - scroll, 3, 40);
            var rot = Quaternion.Euler(pitch, yaw, 0);
            transform.position = orbitCenter + rot * new Vector3(0, 0, -dist);
            transform.rotation = rot;
            if (target) target.position = orbitCenter;
        }
    }
}
