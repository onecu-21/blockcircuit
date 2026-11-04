# Phase 03 검증 — .NET 10

검증일: 2026-11-04 (Asia/Seoul). Windows x64, SDK 10.0.403, 런타임 .NET 10.0.14, Raylib-cs 6.1.0. 사용자 지시에 따라 커밋·푸시는 수행하지 않고 git add까지 진행한다.

## 완료 범위

월드의 실제 기본 저장을 v4 매니페스트와 16³ 청크 파일로 분리했다. Registry 영구 ID 팔레트, 음수 좌표, 콘텐츠 Dirty 세대와 메시 리비전 분리, 빈 청크 삭제, 구형 v1/v2/v3 이행, 원본 백업, 파일 체크섬과 이전 세대 복구를 통합했다. CPU 메시 생성/GPU 갱신은 청크 수명주기 상태·토큰을 사용한다. 상태 취소/실패/재시도와 청크 재생성 사이의 오래된 완료/저장 승인을 거부한다.

현재 플레이어의 이동·생성 영역은 기존 유한 범위다. 저장 API는 int 전체 범위의 셀을 다루지만 스트리밍·백그라운드 메시·먼 좌표 렌더링 정밀도 개선은 Phase 04 작업이다.

주요 변경 파일:

- World.cs: 음수 저장 좌표, 안전한 이웃 좌표, 메타데이터 소유권과 Dirty 연동
- World/Chunk.cs, ChunkCoord.cs, ChunkManager.cs, ChunkLifecycle.cs
- World/ChunkSaveStore.cs, RegistryPersistence.cs, WorldSession.cs
- Rendering/ChunkMesher.cs, BlockRenderer.cs, Circuits/CircuitSystem.cs
- Core/Game.cs, Core/EngineSmokeVerifier.cs
- Tests/ChunkLifecycleTests.cs, ChunkPersistenceTests.cs, StorageBenchmark.cs, TestSaveFiles.cs
- SelfTests.cs, Tests/EngineTests.cs, GameplayTests.cs, TerrainTests.cs
- Program.cs, scripts/validate.ps1, README.md, ENGINE-NOTES.md
- docs/storage.md, registries.md, architecture.md, roadmap.md

## 빌드와 검사 결과

| 검증 | 결과 |
|---|---|
| Debug / Release 빌드 | 각각 경고 0, 오류 0 |
| Phase 01까지의 검사 | 각각 270개 통과 |
| Registry / 설정 검사 | 각각 37 / 16개 통과 |
| 청크 상태 / 저장 검사 | 각각 30 / 44개 통과 |
| 전체 헤드리스 검사 | 각각 397개 통과 |
| Release 그래픽 | 기존 7모드 모두 종료 코드 0 |
| GPU 엔진 | 125프레임, 118개 정지 프레임 재생성·업로드 0회, 21개 메시 전부 해제, 종료 후 VBO 0 |
| 게임플레이 / 조작 그래픽 | 기존 9 / 8개 판정 통과 |

음수·정수 끝 좌표의 floor division/복원과 CPU 메시, Dirty 소유 청크만 저장, 로드 후 clean 재사용, 삭제 유지, 레버/JSON/플레이어/수량/틱 상태, 등록 순서 변경과 사라진 모드 데이터를 검사했다. v1/v2/v3 원본의 byte 단위 백업 보존도 확인했다.

실패 검사는 청크 쓰기 뒤·매니페스트 커밋 전 IOException 주입, Dirty 재시도, SHA-256 손상과 잘못된 참조/경로, 정상 .bak 복구, 양쪽 손상 시 활성 월드 유지, 저장 도중 수정/삭제의 세대 보존을 다룬다. 손상된 Dirty 청크가 있어도 정상 백업을 덮어쓰지 않고, 직접 청크 삭제·재생성으로 오래된 모드 블록 데이터가 되살아나지 않는지 확인했다. 같은 런타임 fallback ID를 유지하는 명시적 교체도 Dirty 저장한다.

기존 저장 거부·회귀 검사는 유지했다. 현재 형식 번호 판정은 v4로 바꾸고 기존 v1/v2 손상 판정은 명시적 SaveLegacy fixture로 계속 실행한다. 임시 저장의 청크 디렉터리와 .bak/.lock도 검사 종료 시 정리한다.

## 실제 성능과 관리 할당

32개 희소 청크에서 5개 표본 중앙값을 측정했다. 전체 경로에는 JSON, 체크섬 검증, 동기 파일 Flush와 정리가 포함된다.

| 작업 | 중앙값 | 관리 할당 | 새 청크 파일 / 재사용 |
|---|---:|---:|---:|
| 전체 저장 | 45.7118 ms | 1,831,984 B | 32 / 0 |
| 변경 없는 저장 | 7.5991 ms | 719,912 B | 0 / 32 |
| 한 청크 수정 저장 | 9.5230 ms | 775,040 B | 1 / 31 |

로드 단일 표본은 40.0601 ms, 관리 할당 2,676,080 B였고 모든 저장 셀이 일치했다. 원본은 [storage-benchmark-results.json](storage-benchmark-results.json)에 있다. 이 값은 해당 장비·작은 희소 배치의 동기 CPU/I/O 측정이며 FPS나 대규모 스트리밍 성능이 아니다.

기존 메시/회로/물리 벤치마크도 통과했다. 메시 캐시의 정지/경계/2,000회 편집 일치를 유지했고, 회로 최종 상태는 독립 참조 구현과 일치했다. 물리 100,000틱에서 관리 할당 0 B였다. 원본 JSON과 로그를 같은 폴더에 보존한다.

전체 저장이 50 ms 틱 예산에 근접할 수 있으므로 여기서는 저장 중 프레임 정지가 없다고 주장하지 않는다. 작업 예산·백그라운드 처리와 대규모/장시간 검증은 후속 Phase 04/12 범위다. 원격 Linux CI나 배포본 실행 결과는 이 로컬 검증에 포함하지 않는다.

## 재현

    ./scripts/validate.ps1 -Configuration Debug -OutputDirectory artifacts/phase03-debug
    ./scripts/validate.ps1 -Configuration Release -OutputDirectory artifacts/phase03-release -Benchmarks -Graphics

세부 파일 형식·제한·복원 절차는 [storage.md](../../storage.md)를 따른다. 사용자 승인 범위인 Phase 03까지 완료했고 다음 단계는 [로드맵](../../roadmap.md)의 Phase 04다.
