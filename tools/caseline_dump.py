"""제함·봉함 라인 동작 값 덤프 (Python ↔ C# 비교용).
  python3 tools/caseline_dump.py > py.txt
C# 쪽은 Assets/CaseLine/Scripts/CaseLineKinematics.cs 를 같은 형식으로 출력하는 작은 프로그램으로 확인합니다 (docs/caseline-research.md)."""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "blender"))
import caseline_kinematics as K

def f(v): return f"{v:.6f}"
for i in range(0, 1200):
    t = i * 0.0317
    out = [f(t), f(K.pusher_x(t)), f(K.kicker_angle(t))] + [f(v) for v in K.pick_arm(t)]
    for k in K.boxes_alive(t):
        tau = t - K.PHASE - k * K.PERIOD
        vis, r = K.box_pose(tau); tt = max(tau, 0.0)
        out += [str(k), f(r[0]), f(r[1]), f(r[2])]
        for nm, c, ex, ey, ez, s in K.box_slabs(tt):
            out += [f(c[0]), f(c[1]), f(c[2]), f(ey[0]), f(ey[1]), f(ey[2]), f(ez[0]), f(ez[1]), f(ez[2])]
        for nm, on, c, s in K.tape_parts(tt):
            out += [str(int(on)), f(c[0]), f(s[0])]
        for j in range(8):
            pv, p, a = K.pack_pose(tt, j)
            out += [str(int(pv)), f(p[0]), f(p[1]), f(p[2]), f(a)]
    print(" ".join(out))
