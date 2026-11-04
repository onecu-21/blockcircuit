# Phase 01 검증 기록 — 2026-11-04

대상: Windows x64, SDK 10.0.403 / 런타임 .NET 10.0.14, Raylib-cs 6.1.0.
선행 기준선 커밋: 445c17b. 현재 단계 커밋 메시지: refactor: finalize 20 tps simulation.

## 완료한 변경

실제 게임과 기본 GameClock을 20 TPS/50 ms로 전환했다. PhysicsProfile에 중력·점프·가속·마찰을 명시하고 PlayerBody에 수평 운동량과 이동 후 수직 감쇠를 적용했다. 프레임별 입력은 SimulationInput으로 전달하며 짧은 점프와 비행 전환은 틱까지 보존한다. 기존 위치 보간과 프레임별 마우스 시점을 유지한다. 240 FPS 설정/저장/설정 UI를 추가했다. 저장 형식은 v1/v2를 유지한다.

주요 변경 파일:

- Core/Game.cs, GameClock.cs, GameSimulation.cs, SimulationInput.cs, GameplaySmoke.cs
- Core/GameSettings.cs, GameSettingsPanel.cs
- Physics/PlayerBody.cs, PhysicsProfile.cs
- Tests/PhysicsFoundationTests.cs, PhysicsBenchmark.cs, GameplayTests.cs, SneakTests.cs
- Program.cs, scripts/validate.ps1, .gitignore, README.md, ENGINE-NOTES.md, docs/physics.md, architecture.md, roadmap.md

Windows에서 기존 /core 덤프 무시 규칙이 새 Core 소스도 제외하므로 디렉터리 예외를 추가했다. 검증 출력은 artifacts/build에 분리하여 실행 중인 개발용 EXE 잠금과 충돌하지 않는다.

## 실행 결과

| 검증 | 결과 |
|---|---|
| Debug / Release 빌드 | 각각 경고 0, 오류 0 |
| 기존 헤드리스 검사 | 각각 229/229 통과 |
| 새 물리/입력/FPS 검사 | 각각 41/41 통과 |
| 전체 헤드리스 검사 | 각각 270/270 통과 |
| Release 그래픽 | smoke/palette/showcase/terrain/engine/gameplay/controls 7개 모두 종료 코드 0 |
| 엔진 OpenGL 검사 | 125프레임, 118개 정지 프레임 재생성/업로드 0회, 할당 21개 전부 해제, 종료 후 VBO 0 |
| 게임플레이 OpenGL 검사 | 60 FPS 입력 시간 / 20 TPS에서 90프레임 중 25틱, 기존 9개 판정 통과 |
| 조작 OpenGL 검사 | 기존 8개 판정 통과 |
| 설정 화면 시각 확인 | 240 FPS 포함 6개 버튼과 문구가 패널 안에 표시됨 |

새 검사는 30/60/144/240 FPS에서 30초 동안 정확한 600틱, 불규칙 프레임 누적, 80틱 전체 궤적 동등성, 틱 사이 점프/비행 입력, 점프 높이 약 1.2522블록, 정상 속도와 마찰, 벽/천장/절벽/비행/텔레포트, 240 FPS 설정 복원을 확인한다.

모든 기존 검사를 유지했다. 절벽 점프/걷기 검사는 가속 후 이탈을 관찰하도록 기존 0.1초를 0.25/0.2초로 늘렸고 공간·추락·지지면 판정은 유지했다. 그래픽 이벤트 판정은 명령 다음의 실제 20 Hz 틱 프레임으로 옮겼다.

## CPU와 관리 할당

물리 벤치마크는 10,000틱 준비 후 100,000틱을 측정했다. 합계 121.4799 ms, 관리 할당 0 B, 중앙값/P95/P99는 1.1/1.7/1.9 µs였다. 원본은 [physics-benchmark-results.json](physics-benchmark-results.json)이다. 측정에는 계측과 충돌 상태 검증 비용도 포함한다.

메시/회로 벤치마크도 통과했다. 정지 300프레임의 메시 재생성 0회, 내부/경계 편집 1/2개 청크 갱신, 2,000회 편집 후 20회 전체 비교 일치, 회로 최종 전원 참조 구현 일치를 확인했다. 원본 JSON과 로그를 이 폴더에 보존한다. 런타임/장비/JIT 잡음이 있으므로 이전 표본보다 빨라졌다는 최적화 주장은 하지 않는다.

물리 호출 P95/P99는 렌더링 프레임 시간이 아니다. 장시간 상주 메모리, 새 스트리밍/유체/AI 성능, 원격 Linux CI와 배포본 실행은 이번 단계에서 측정하지 않았다. 프로필의 참고 자료·지원 범위는 [physics.md](../../physics.md)를 따른다.

## 재현과 다음 단계

    ./scripts/validate.ps1 -Configuration Debug -OutputDirectory artifacts/phase01-debug
    ./scripts/validate.ps1 -Configuration Release -OutputDirectory artifacts/phase01-release -Benchmarks -Graphics

각 명령의 전체 출력은 Debug-*.log / Release-*.log에 있다. 사용자의 월드/설정은 스모크 실행에서 저장하지 않는다. 다음 구현은 [로드맵](../../roadmap.md)의 Phase 02 Registry/Event이며 Phase 02–13은 미구현이다.
