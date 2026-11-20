// 통합 HUD 의 설비 라이브러리: 지금까지 만든 설비 장면 11개의 이름·설명·장면 경로·대시보드 KPI·조정 파라미터.
// 새 설비 장면을 만들면 여기 All 목록에 한 줄(Add<View>(...)) 추가하면 HUD 라이브러리에 나타납니다.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace EquipFX
{
    public sealed class KpiDef
    {
        public string label, unit;
        public Func<Component, string> value;   // 아직 준비 전이면 null 반환
    }

    // 속성 패널에서 바꿀 수 있는 설정값 (적용하면 장면을 다시 시작)
    public sealed class ParamDef
    {
        public string field, label;   // field: view.config 의 필드 이름 (view 자신의 필드면 "view." 접두사)
        public double min, max;
    }

    // 클릭해 속성을 볼 수 있는 하위 오브젝트 묶음: View 의 Transform 목록 필드 ↔ 시뮬레이션의 같은 순서 목록
    public sealed class PartDef
    {
        public string label;      // "로봇"
        public string viewField;  // View 의 List<Transform> / Transform[] / 루트 Transform 을 가진 객체 목록 필드 이름
        public string simList;    // Sim 의 같은 순번 목록 (없으면 null → 위치만 표시)
    }

    public sealed class EquipEntry
    {
        public string id, name, korean, category, desc, scenePath, rootName;
        public Type viewType, captureType;
        public string menuType;                        // 에디터 메뉴 클래스 (녹화 단추가 Record() 를 부름)
        public string throughputUnit = "/h";
        public Func<Component, bool> ready;            // 시뮬레이션이 만들어졌는지
        public Func<Component, double> simTime;        // 시뮬레이션 시각 (s)
        public Func<Component, double> throughput;     // 누적 평균 처리량 (/h)
        public Func<Component, double> doneCount;      // 누적 처리 건수 (최근 처리량 그래프용)
        public Func<Component, int> robotCount;        // 이동 장비(로봇·셔틀·크레인) 수
        public readonly List<KpiDef> kpis = new List<KpiDef>();
        public readonly List<ParamDef> pars = new List<ParamDef>();
        public readonly List<PartDef> parts = new List<PartDef>();
    }

    public static class EquipCatalog
    {
        static List<EquipEntry> all;
        public static List<EquipEntry> All => all ??= Build();

        public static EquipEntry Find(Component view)
        {
            if (view == null) return null;
            foreach (var e in All) if (e.viewType == view.GetType()) return e;
            return null;
        }

        // ------------------------------------------------------------------ 목록
        static List<EquipEntry> Build()
        {
            var L = new List<EquipEntry>();

            // 1) AutoStore -------------------------------------------------
            {
                var b = new B<global::AutoStore.AutoStoreView>(L, "autostore", "AutoStore", "큐브형 자동창고 (그리드 상부 로봇)", "큐브 스토리지 · ASRS",
                    "빈을 수직으로 쌓은 그리드 위를 로봇이 달리며, 그리퍼로 스택 안의 빈을 꺼내(디깅) 포트로 가져다 줍니다.",
                    ScenePaths.AutoStore, "AutoStore", typeof(global::AutoStore.MovieCapture), "AutoStore.EditorTools.AutoStoreMenu", "bins/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots"); b.Part("빈", "binT", "binLoc");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("포트", "개", v => v.Sim.ports.Count.ToString());
                b.K("보관 빈", "개", v => v.Sim.bins.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("평균 리드타임", "s", v => v.Sim.AvgLead.ToString("0"));
                b.K("디깅 / 인출", "회", v => v.Sim.DigsPerFetch.ToString("0.00"));
                b.K("로봇 가동률", "", v => Pct(v.Sim.RobotUtil));
                b.K("포트 가동률", "", v => Pct(v.Sim.PortUtil));
                b.P("robots", "로봇 수", 1, 20); b.P("orderRate", "주문 (빈/h)", 30, 1500); b.P("fill", "채움률", 0.3, 0.95); b.P("seed", "난수 시드", 0, 999);
            }
            // 2) Libiao 3D 소터 --------------------------------------------
            {
                var b = new B<global::Libiao.LibiaoView>(L, "libiao", "Libiao 3D Sorter", "3D 로봇 소터 (데크 + 자루 투하)", "소터 · 택배 분류",
                    "높은 데크 위를 소형 로봇이 소포를 싣고 달려, 목적지 구멍 위에서 트레이를 기울여 아래 자루로 떨어뜨립니다.",
                    ScenePaths.Libiao, "LibiaoSorter", typeof(global::Libiao.LibiaoMovieCapture), "Libiao.EditorTools.LibiaoMenu", "parcels/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.delivered, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("슈트", "개", v => v.Sim.chutes.Count.ToString());
                b.K("투입 스테이션", "개", v => v.Sim.stations.Count.ToString());
                b.K("분류 완료", "건", v => v.Sim.delivered.ToString());
                b.K("이동 중 소포", "개", v => v.Sim.alive.Count.ToString());
                b.K("로봇 정체율", "", v => Pct(v.Sim.BlockedShare));
                b.P("robots", "로봇 수", 1, 45); b.P("seed", "난수 시드", 0, 999);
            }
            // 3) T-Sorter --------------------------------------------------
            {
                var b = new B<global::TSorter.TSorterView>(L, "tsorter", "T-Sort Finger Sorter", "핑거 플랫폼 로봇 소터", "소터 · 택배 분류",
                    "빗살(핑거) 모양 플랫폼 위를 로봇이 달려, 핑거 사이 슈트 통으로 소포를 떨어뜨리고 가득 찬 통은 교체합니다.",
                    ScenePaths.TSorter, "TSorterLine", typeof(global::TSorter.TSorterMovieCapture), "TSorter.EditorTools.TSorterMenu", "parcels/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.delivered, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("슈트", "개", v => v.Sim.chutes.Count.ToString());
                b.K("투입 스테이션", "개", v => v.Sim.stations.Count.ToString());
                b.K("분류 완료", "건", v => v.Sim.delivered.ToString());
                b.K("통 교체", "회", v => v.Sim.Swaps.ToString());
                b.K("로봇 정체율", "", v => Pct(v.Sim.BlockedShare));
                b.P("robots", "로봇 수", 1, 45); b.P("seed", "난수 시드", 0, 999);
            }
            // 4) AirRob ----------------------------------------------------
            {
                var b = new B<global::AirRob.AirRobView>(L, "airrob", "AirRob", "랙 상부 레일 로봇 + 바닥 로봇", "토트 창고 · ASRS",
                    "랙 위 레일을 달리는 AirRob 이 토트를 꺼내 터널로 내려 주고, 바닥 로봇이 받아 피킹 스테이션으로 나릅니다.",
                    ScenePaths.AirRob, "AirRobWarehouse", typeof(global::AirRob.AirRobMovieCapture), "AirRob.EditorTools.AirRobMenu", "totes/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.airs.Count + v.Sim.robots.Count);
                b.Part("랙 상부 로봇", "airT", "airs"); b.Part("바닥 로봇", "botT", "robots"); b.Part("토트", "toteT", "toteLoc");
                b.K("AirRob", "대", v => v.Sim.airs.Count.ToString());
                b.K("바닥 로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("보관 토트", "개", v => v.Sim.totes.ToString());
                b.K("처리 완료", "건", v => v.Sim.statDone.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("평균 리드타임", "s", v => v.Sim.AvgLead.ToString("0"));
                b.K("AirRob 가동률", "", v => Pct(v.Sim.AirUtil));
                b.K("바닥 로봇 가동률", "", v => Pct(v.Sim.BotUtil));
                b.K("스테이션 가동률", "", v => Pct(v.Sim.StationUtil));
                b.P("bots", "바닥 로봇 수", 1, 16); b.P("orderRate", "주문 (토트/h)", 30, 1200); b.P("seed", "난수 시드", 0, 999);
            }
            // 5) Rapyuta ---------------------------------------------------
            {
                var b = new B<global::Rapyuta.RapyutaView>(L, "rapyuta", "Rapyuta ASRS", "다층 선반형 로봇 창고", "큐브 스토리지 · ASRS",
                    "층마다 로봇이 빈 아래로 들어가 들어 올려 나르고, 리프트가 로봇째 층 사이를 오르내려 피킹층으로 보냅니다.",
                    ScenePaths.Rapyuta, "RapyutaASRS", typeof(global::Rapyuta.RapyutaMovieCapture), "Rapyuta.EditorTools.RapyutaMenu", "bins/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots"); b.Part("리프트", "liftT", "lifts"); b.Part("빈", "binT", "binLoc");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("리프트", "대", v => v.Sim.lifts.Count.ToString());
                b.K("층", "층", v => v.config.nf.ToString());
                b.K("보관 빈", "개", v => v.Sim.bins.ToString());
                b.K("처리 완료", "건", v => v.Sim.statDone.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("디깅 / 인출", "회", v => v.Sim.DigsPerFetch.ToString("0.00"));
                b.K("로봇 가동률", "", v => Pct(v.Sim.RobotUtil));
                b.K("스테이션 가동률", "", v => Pct(v.Sim.StationUtil));
                b.P("robots", "로봇 수", 1, 40); b.P("orderRate", "주문 (빈/h)", 30, 1500); b.P("seed", "난수 시드", 0, 999);
            }
            // 6) HaiPick ---------------------------------------------------
            {
                var b = new B<global::HaiPick.HaiPickView>(L, "haipick", "HaiPick Climb", "랙 등반 로봇 (ACR)", "토트 창고 · ACR",
                    "로봇이 통로를 달리다 랙 기둥을 타고 올라가 높은 단의 토트를 직접 꺼내 작업대로 가져옵니다.",
                    ScenePaths.HaiPick, "HaiPickClimb", typeof(global::HaiPick.HaiPickMovieCapture), "HaiPick.EditorTools.HaiPickMenu", "totes/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots"); b.Part("토트", "toteT", "toteLoc");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("보관 토트", "개", v => v.Sim.totes.ToString());
                b.K("처리 완료", "건", v => v.Sim.statDone.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("평균 리드타임", "s", v => v.Sim.AvgLead.ToString("0"));
                b.K("평균 등반", "m", v => v.Sim.AvgClimb.ToString("0.0"));
                b.K("로봇 가동률", "", v => Pct(v.Sim.RobotUtil));
                b.K("스테이션 가동률", "", v => Pct(v.Sim.StationUtil));
                b.P("robots", "로봇 수", 1, 40); b.P("orderRate", "주문 (토트/h)", 30, 1500); b.P("seed", "난수 시드", 0, 999);
            }
            // 7) Skypod ----------------------------------------------------
            {
                var b = new B<global::Skypod.SkypodView>(L, "skypod", "Skypod", "랙 등반 로봇 (3D 이동)", "토트 창고 · ASRS",
                    "바닥을 달리던 로봇이 그대로 랙을 타고 올라가 토트를 꺼낸 뒤, 내려와 피킹 스테이션으로 줄지어 들어갑니다.",
                    ScenePaths.Skypod, "SkypodWarehouse", typeof(global::Skypod.SkypodMovieCapture), "Skypod.EditorTools.SkypodMenu", "totes/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.robots.Count);
                b.Part("로봇", "robotT", "robots"); b.Part("토트", "toteT", "toteLoc");
                b.K("로봇", "대", v => v.Sim.robots.Count.ToString());
                b.K("보관 토트", "개", v => v.Sim.totes.ToString());
                b.K("처리 완료", "건", v => v.Sim.statDone.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("평균 리드타임", "s", v => v.Sim.AvgLead.ToString("0"));
                b.K("평균 등반", "m", v => v.Sim.AvgClimb.ToString("0.0"));
                b.K("로봇 가동률", "", v => Pct(v.Sim.RobotUtil));
                b.K("스테이션 가동률", "", v => Pct(v.Sim.StationUtil));
                b.P("robots", "로봇 수", 1, 40); b.P("orderRate", "주문 (토트/h)", 30, 1500); b.P("seed", "난수 시드", 0, 999);
            }
            // 8) 셔틀랙 ----------------------------------------------------
            {
                var b = new B<global::ShuttleRack.ShuttleView>(L, "shuttle", "Shuttle AS/RS", "셔틀 랙 (단별 셔틀 + 리프트)", "토트 창고 · 셔틀",
                    "단마다 셔틀이 레일을 달려 토트를 꺼내고, 통로 앞 리프트가 1층 컨베이어로 내려 스테이션까지 보냅니다.",
                    ScenePaths.Shuttle, "ShuttleRackWarehouse", typeof(global::ShuttleRack.ShuttleMovieCapture), "ShuttleRack.EditorTools.ShuttleMenu", "totes/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.ThroughputPerHour, v => v.Sim.statDone, v => v.Sim.shuttles.Count);
                b.Part("셔틀", "shT", "shuttles"); b.Part("리프트", "carT", "lifts"); b.Part("토트", "toteT");
                b.K("셔틀", "대", v => v.Sim.shuttles.Count.ToString());
                b.K("리프트", "대", v => v.Sim.lifts.Count.ToString());
                b.K("보관 토트", "개", v => v.Sim.totes.ToString());
                b.K("처리 완료", "건", v => v.Sim.statDone.ToString());
                b.K("대기 주문", "건", v => v.Sim.Backlog.ToString());
                b.K("평균 리드타임", "s", v => v.Sim.AvgLead.ToString("0"));
                b.K("셔틀 가동률", "", v => Pct(v.Sim.ShuttleUtil));
                b.K("리프트 가동률", "", v => Pct(v.Sim.LiftUtil));
                b.K("스테이션 가동률", "", v => Pct(v.Sim.StationUtil));
                b.P("orderRate", "주문 (토트/h)", 100, 3600); b.P("seed", "난수 시드", 0, 999);
            }
            // 9) 캐러셀 ----------------------------------------------------
            {
                var b = new B<global::Carousel.CarouselView>(L, "carousel", "Carousel Picking", "수평·수직 회전 선반", "피킹 · 캐러셀",
                    "선반이 회전해 원하는 빈을 작업자 앞으로 가져옵니다. 수평형 2대가 한 작업자 포드, 수직형 1대가 별도 작업자입니다.",
                    ScenePaths.Carousel, "CarouselPicking", typeof(global::Carousel.CarouselMovieCapture), "Carousel.EditorTools.CarouselMenu", "lines/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.HLinesPerHour + v.Sim.VLinesPerHour,
                    v => (v.Sim.HLinesPerHour + v.Sim.VLinesPerHour) * v.Sim.t / 3600.0, v => v.config.hCount + 1);
                b.Part("수평 캐러셀 선반", "hCar", "units"); b.Part("수직 캐러셀 트레이", "vTray");
                b.K("수평형 처리량", "lines/h", v => v.Sim.HLinesPerHour.ToString("0"));
                b.K("수직형 처리량", "lines/h", v => v.Sim.VLinesPerHour.ToString("0"));
                b.K("수평형 리드타임", "s", v => v.Sim.HLead.ToString("0"));
                b.K("수직형 리드타임", "s", v => v.Sim.VLead.ToString("0"));
                b.K("수평 작업자 가동률", "", v => Pct(v.Sim.HOpUtil));
                b.K("수평 캐러셀 가동률", "", v => Pct(v.Sim.HCarouselUtil));
                b.K("수직 작업자 가동률", "", v => Pct(v.Sim.VOpUtil));
                b.K("수직 캐러셀 가동률", "", v => Pct(v.Sim.VCarouselUtil));
                b.P("hOrderRate", "수평형 주문 (lines/h)", 30, 800); b.P("vOrderRate", "수직형 주문 (lines/h)", 30, 600); b.P("seed", "난수 시드", 0, 999);
            }
            // 10) 스태커크레인 ---------------------------------------------
            {
                var b = new B<global::StackerCrane.StackerCraneView>(L, "stacker", "Stacker Crane", "팔레트 자동창고 (유닛로드 AS/RS)", "팔레트 창고 · AS/RS",
                    "통로마다 스태커크레인이 주행·승강하며 텔레스코픽 포크로 팔레트를 입고·출고합니다. 입출고를 묶은 이중 사이클로 효율을 높입니다.",
                    ScenePaths.StackerCrane, "StackerCraneWarehouse", typeof(global::StackerCrane.StackerCraneMovieCapture), "StackerCrane.EditorTools.StackerCraneMenu", "moves/h");
                b.Core(v => v.Sim != null, v => v.Sim.t, v => v.Sim.MovesPerHour, v => v.Sim.statStored + v.Sim.statRetrieved, v => v.config.aisles);
                b.Part("스태커 크레인", "craneT", "cranes"); b.Part("팔레트", "palT", "pal");
                b.K("크레인", "대", v => v.config.aisles.ToString());
                b.K("보관률", "", v => Pct(v.Sim.Occupancy));
                b.K("입고", "건", v => v.Sim.statStored.ToString());
                b.K("출고", "건", v => v.Sim.statRetrieved.ToString());
                b.K("이중 사이클", "", v => Pct(v.Sim.DualShare));
                b.K("평균 사이클", "s", v => v.Sim.AvgCycle.ToString("0"));
                b.K("크레인 가동률", "", v => Pct(v.Sim.CraneUtil));
                b.P("inRate", "입고 (팔레트/h·통로)", 5, 120); b.P("outRate", "출고 (팔레트/h·통로)", 5, 120); b.P("seed", "난수 시드", 0, 999);
            }
            // 11) 제함·봉함 라인 -------------------------------------------
            {
                var b = new B<global::CaseLine.CaseLineView>(L, "caseline", "Case Erect · Seal Line", "반자동 제함 → 충전 → 봉함", "포장 라인",
                    "로봇 팔이 납작한 박스를 세워 제함하고, 컨베이어를 따라 제품을 채운 뒤 테이프로 봉함합니다.",
                    ScenePaths.CaseLine, "CaseLine", typeof(global::CaseLine.CaseLineMovieCapture), "CaseLine.EditorTools.CaseLineMenu", "box/h");
                b.Core(v => v.Cam != null || v.moduleMode, v => v.T, v => 3600.0 / global::CaseLine.K.PERIOD, v => CaseDone(v.T), v => 1);
                b.K("박스 규격", "mm", v => $"{(global::CaseLine.K.L * 1000):0}×{(global::CaseLine.K.W * 1000):0}×{(global::CaseLine.K.H * 1000):0}");
                b.K("투입 간격", "s", v => global::CaseLine.K.PERIOD.ToString("0.0"));
                b.K("컨베이어", "m/min", v => (global::CaseLine.K.V * 60).ToString("0"));
                b.K("완성 박스", "개", v => CaseDone(v.T).ToString("0"));
            }
            return L;
        }

        static double CaseDone(double t) { global::CaseLine.K.Cycle(t, out long k, out _); return Math.Max(0, k - 2); }

        public static string Pct(double v) => (v * 100).ToString("0") + "%";

        // 항목 만들기 도우미 (뷰 타입별로 람다를 안전하게 감쌈)
        sealed class B<TView> where TView : Component
        {
            readonly EquipEntry e;
            public B(List<EquipEntry> list, string id, string name, string korean, string category, string desc,
                     string scenePath, string rootName, Type capture, string menuType, string unit)
            {
                e = new EquipEntry
                {
                    id = id, name = name, korean = korean, category = category, desc = desc, scenePath = scenePath,
                    rootName = rootName, viewType = typeof(TView), captureType = capture, menuType = menuType, throughputUnit = unit,
                };
                list.Add(e);
            }
            static Func<Component, T> Wrap<T>(Func<TView, T> f, T fallback) => c =>
            {
                var v = c as TView; if (v == null) return fallback;
                try { return f(v); } catch (Exception) { return fallback; }
            };
            public void Core(Func<TView, bool> ready, Func<TView, double> t, Func<TView, double> tp, Func<TView, double> done, Func<TView, int> robots)
            {
                e.ready = Wrap(ready, false);
                e.simTime = Wrap(t, 0.0);
                e.throughput = Wrap(tp, 0.0);
                e.doneCount = Wrap(done, 0.0);
                e.robotCount = Wrap(robots, 0);
            }
            public void K(string label, string unit, Func<TView, string> val)
            {
                var ready = e.ready;
                var w = Wrap(val, null);
                e.kpis.Add(new KpiDef { label = label, unit = unit, value = c => ready != null && ready(c) ? w(c) : null });
            }
            public void Part(string label, string viewField, string simList = null) =>
                e.parts.Add(new PartDef { label = label, viewField = viewField, simList = simList });
            public void P(string field, string label, double min, double max) =>
                e.pars.Add(new ParamDef { field = field, label = label, min = min, max = max });
        }
    }
}

namespace EquipFX
{
    // 각 설비 에디터 메뉴가 만드는 장면 파일 경로 (Editor 어셈블리의 *Menu.ScenePath 와 같아야 합니다)
    public static class ScenePaths
    {
        public const string AutoStore = "Assets/AutoStore/Scenes/AutoStoreSim.unity";
        public const string Libiao = "Assets/Libiao/Scenes/LibiaoSorter.unity";
        public const string TSorter = "Assets/TSorter/Scenes/TSorterLine.unity";
        public const string AirRob = "Assets/AirRob/Scenes/AirRobWarehouse.unity";
        public const string Rapyuta = "Assets/Rapyuta/Scenes/RapyutaASRS.unity";
        public const string HaiPick = "Assets/HaiPick/Scenes/HaiPickClimb.unity";
        public const string Skypod = "Assets/Skypod/Scenes/SkypodWarehouse.unity";
        public const string Shuttle = "Assets/ShuttleRack/Scenes/ShuttleRackWarehouse.unity";
        public const string Carousel = "Assets/Carousel/Scenes/CarouselPicking.unity";
        public const string StackerCrane = "Assets/StackerCrane/Scenes/StackerCraneWarehouse.unity";
        public const string CaseLine = "Assets/CaseLine/Scenes/CaseLine.unity";
    }
}
