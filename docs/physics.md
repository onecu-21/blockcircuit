# 20 TPS 물리와 입력 — Phase 01

대상은 C# / .NET 10이다. Phase 00의 확장 전 구조와 측정은 [architecture.md](architecture.md)와 [기준선](baseline/2026-11-04/README.md)에 보존한다.

## 시간과 입력 계약

GameClock의 기본 속도와 실제 Game 루프는 20 TPS, 정확히 50 ms 간격이다. 내부 누적 시간은 double이고 물리 콜백에는 고정 float 0.05를 전달한다. 렌더 FPS는 30/60/120/144/240/무제한 중 선택한다. 입력 프레임 시간은 최대 250 ms, 따라잡기는 최대 8틱으로 제한하고 버린 시간은 DroppedSeconds에 기록한다. 기본 20 TPS에서 250 ms는 5틱이다. 메뉴/로드는 누적 시간과 입력을 초기화한다.

CaptureInput은 매 렌더 프레임 마우스 yaw/pitch를 갱신한다. SimulationInput은 최신 이동/유지 상태와 다음 틱까지 남는 점프 눌림을 저장한다. 짧게 누르고 떼어도 한 번 소비하고, 계속 누르면 착지 뒤 다시 점프한다. 비행 제스처는 ToggleFlight를 대기시켜 다음 SimulationTick 시작에 적용한다. 두 번 대기한 전환은 상쇄된다. 설치/레버/실행 취소/재실행은 기존 액션 큐를 유지한다.

렌더링의 눈 위치와 아바타는 previousFeet/currentFeet를 clock.Alpha로 보간한다. 시점 회전은 프레임별로 즉시 반영한다. 보간은 최대 한 틱의 위치 지연을 가지며 카메라 회전의 대기는 만들지 않는다.

## 단위와 이동 프로필

PhysicsProfile은 블록/초 단위 API와 20 Hz 기준 상수를 한 곳에 정의한다. 참고 버전은 Minecraft Java 1.21.1이다. [Mojang의 기본 20 TPS 설명](https://www.minecraft.net/en-us/article/minecraft-java-edition-1-20-3), [Fabric Yarn 1.21.1 상수](https://maven.fabricmc.net/docs/yarn-1.21.1%2Bbuild.3/constant-values.html), [NeoForge 1.21.1 LivingEntity 패치의 마찰 문맥](https://raw.githubusercontent.com/neoforged/NeoForge/1.21.1/patches/net/minecraft/world/entity/LivingEntity.java.patch)을 대조했다.

| 항목 | 채택 값 | 근거/검증 |
|---|---|---|
| 중력 | 0.08 block/tick² = 32 block/s² | Yarn LivingEntity.GRAVITY, 단위 변환 |
| 점프 초기 속도 | 0.42 block/tick = 8.4 block/s | BlockCircuit 프로필; 독립 수치 궤적으로 검증 |
| 수직 감쇠 | 0.98/tick | BlockCircuit 프로필; 자유낙하/점프 궤적으로 검증 |
| 공중 수평 감쇠 | 0.91/tick | NeoForge 원본 문맥의 수평 계수 |
| 기본 지면 감쇠 | 0.6 × 0.91 = 0.546/tick | Yarn DEFAULT_FRICTION과 위 계수로 계산 |
| 기본 가속 기준 | 0.098 block/tick | BlockCircuit 프로필 |
| 정상 걷기 | 약 4.31718 block/s | 0.098/(1−0.546)×20; 실제 정상 속도 검사 |
| 달리기/웅크리기 | 걷기의 1.3/0.3배 | 약 5.61233/1.29515 block/s, 실제 속도 검사 |
| 공중 가속 | 0.02 block/tick 기준 | 입력 속도 비율에 따라 조절 |
| 점프 유지 재시도 | 0.5초 | 낮은 천장에서 매 틱 점프 재설정 방지 |

기본 중력과 지면 마찰은 위 1차 자료로 확인했다. 점프 강도·수직 감쇠·입력 가속은 이 엔진에서 명시적으로 채택하고 검사한 프로필 값이다. Java 1.21.1 전체 물리와 비트 단위 일치로 검증한 것은 아니다. 기존 충돌 반폭 0.29/높이 1.75/웅크린 높이 1.5와 비행 속도·유한 월드 경계를 유지한다. 재질별 미끄러움, 계단 자동 오르기, 달리기 점프의 별도 수평 부스트는 후속 조작감 개선 범위다.

정상 틱의 수직 계산 순서는 이동 → 충돌 속도 제거 → 중력 → 감쇠다.

    position.Y += velocity.Y * 0.05
    velocity.Y = (velocity.Y - 32 * 0.05) * 0.98

일반 지면은 입력 방향을 정규화하고 수평 속도를 가속한 뒤 이동/감쇠한다. 입력을 놓으면 관성이 남았다가 마찰로 정지한다. 충돌한 축은 수평 운동량을 0으로 만든다. 비행 전환/텔레포트는 이전 운동량을 지운다. 웅크린 지상 이동은 기존 지지면 검사로 절벽/모서리를 지킨다.

게임은 항상 50 ms를 전달한다. 도구/기존 검사에서 더 긴 dt를 쓰면 최대 8회로 나누어 처리하고 swept 충돌을 유지한다. 임의의 큰 dt에 대해 실제 50 ms 틱과 동일한 궤적을 보장하지 않는다. 이 경로는 벽 통과와 무한 반복을 방지하는 호환 경로다.

## 검증과 측정

기존 229개 검사와 새 20 TPS 검사를 Debug/Release에서 실행한다. 새 검사는 30/60/144/240 FPS의 30초 누적 틱 수, 불규칙 프레임, 따라잡기/일시정지, 매 틱 궤적, 틱 사이 점프 입력, 비행 전환, 중력/마찰/속도, 충돌/절벽을 검사한다. 기존 절벽 테스트의 공간 판정은 유지하고 가속 후 이탈에 필요한 관찰 시간만 늘렸다.

    ./scripts/validate.ps1 -Configuration Debug -OutputDirectory artifacts/phase01-debug
    ./scripts/validate.ps1 -Configuration Release -OutputDirectory artifacts/phase01-release -Benchmarks -Graphics

검증 빌드는 artifacts/build/<Configuration>에 분리하여 실행 중인 개발용 게임의 apphost 잠금을 피한다. 검사는 임시 저장 파일을 사용하고 그래픽 모드는 사용자의 월드/설정을 저장하지 않는다. 현재 원격 Linux CI와 배포본 실행 결과는 이 단계의 로컬 결과에 포함하지 않는다.

--physics-benchmark는 준비 1만틱 뒤 걷기/달리기/벽 충돌/점프/웅크리기 10만틱의 CPU 시간과 현재 스레드 관리 할당을 기록한다. 타이밍에는 검사/계측 비용도 포함한다. P95/P99는 단일 물리 호출의 관측값이며 렌더 프레임 시간이 아니다. 최종 결과는 [Phase 01 검증 기록](validation/phase01/README.md)에 있다.

Phase 04의 실제 플레이는 스트리밍 범위에서 음수/40블록 밖으로 이동하고, 미로딩 지형을 충돌 경계로 처리한다. 비행 발 높이 상한은255이며 기존 유한 월드 검사 경로의36 상한은 호환성 검사를 위해 유지한다. 자세한 한계는 [streaming.md](streaming.md)를 따른다.
