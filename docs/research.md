# AutoStore 조사 요약

AutoStore는 알루미늄 그리드 안에 빈(bin)을 수직으로 쌓고, 그리드 상부 레일을 달리는 로봇이 빈을 꺼내 포트(작업자 스테이션)로 가져다주는 큐브형 자동창고(ASRS)입니다. 시스템은 Grid, Bins, Robots, Ports, Controller 다섯 모듈로 구성됩니다.

## 공식 수치 (모델에 사용)

| 항목 | 값 | 출처 |
|---|---|---|
| 빈 높이 | 220 / 330 / 425 mm | [Bins](https://www.autostoresystem.com/system/bins) |
| 최대 적층 | 220 mm×26, 330 mm×18, 425 mm×14 (약 6 m) | [Bins](https://www.autostoresystem.com/system/bins), [Grid](https://www.autostoresystem.com/system/grid) |
| 빈 최대 하중 | 30 kg | [Bins](https://www.autostoresystem.com/system/bins) |
| 로봇 최고 속도 | 3.1 m/s | [R5](https://www.autostoresystem.com/system/robot-r5) |
| 로봇 가속도 | 0.8 m/s² | [R5](https://www.autostoresystem.com/system/robot-r5) |
| 그리퍼 승강 속도 | 1.6 m/s | [R5 FAQ](https://www.autostoresystem.com/system/robot-r5) |
| R5 크기 (W×L×H) | 700 × 988 × 545 mm, 145~159 kg | [R5](https://www.autostoresystem.com/system/robot-r5) |
| 그리드 위 여유 공간 | 600 mm | [Grid](https://www.autostoresystem.com/system/grid) |
| 평균 디깅 깊이 | 약 2.6단 (27초 미만) | [Solution FAQ](https://www.autostoresystem.com/solution-faqs) |
| 로봇 1대 처리량 | 시간당 30+ 회 빈 배송 | [Solution FAQ](https://www.autostoresystem.com/solution-faqs) |

## 동작 방식 (시뮬레이션 로직에 반영)

- 로봇은 X/Y 두 바퀴 세트를 번갈아 내려 네 방향으로 이동합니다. 방향 전환에는 시간이 듭니다(모델 1.0 s).
- 요청 빈 위에 다른 빈이 있으면 로봇이 위의 빈을 하나씩 들어 근처 스택 위에 옮겨 놓습니다(디깅).
- 포트에서 피킹이 끝난 빈은 원래 자리가 아니라 가까운 빈 스택 맨 위에 돌아갑니다. 그래서 자주 쓰는 빈은 위로, 안 쓰는 빈은 아래로 자연스럽게 정렬됩니다.
- 라우터가 로봇 경로를 계속 재계산하며 충돌을 피합니다. 모델은 셀 예약 + 방향전환 비용이 들어간 다익스트라 경로 + 막힘 시 우회/비켜서기로 근사했습니다.

## 근사/가정

- 빈 바닥 치수(약 649×449 mm)와 그리드 셀 피치(0.70×0.50 m)는 공식 페이지에 수치가 없어 업계 자료 기반 근사입니다.
- 로봇은 단일 셀 점유(B1/R5 캐비티형)로 단순화했습니다.
- 포트 피킹 시간은 6~12 s 균등분포, 주문은 포아송 도착, 수요는 20% 빈이 80% 차지(ABC)로 가정했습니다.

## 시뮬레이션 결과 (12×8 그리드, 330 mm 빈 16단, 포트 3개, 1시간)

| 로봇 | 처리량 (빈/h) | 평균 대기 (s) | 디깅/인출 | 로봇 가동률 |
|---|---|---|---|---|
| 6 | 185 | 640~770 | 3.2 | 99% |
| 8 | 199~200 | 660~760 | 3.0~3.3 | 97~99% |
| 10 | 176 | 794 | 2.9 | 97% |

이 작은 그리드에서는 로봇 8대 부근에서 포화합니다. 로봇을 더 넣으면 포트 앞 셀과 상부 혼잡으로 처리량이 오히려 줄어듭니다. 로봇 1대당 인출 25~31회/h는 공식 "30+ 회"와 비슷한 수준입니다.
