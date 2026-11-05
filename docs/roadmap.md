# Major Engine & Gameplay Expansion 로드맵

대상: C# / .NET 10, Raylib-cs / OpenGL, Windows x64 우선과 Linux 호환 유지.
상태 기준: Phase 05, IP 멀티플레이 및 처음 실행·월드 생성 UX 검증 완료 시점, 2026-11-06 (Asia/Seoul).

## 공통 진행 규칙

Phase 00부터 번호 순서로 진행한다. 각 단계의 완료 조건과 전체 회귀 검증을 통과하기 전에 다음 단계의 본격 구현을 시작하지 않는다. 각 단계에서 기존 코드 확인 → 자료구조/API 설계 → 필요한 테스트 → 최소 구현 → 통합 → 전체 검사 → 성능/메모리 검사 → 문서 갱신 → 스테이징 순서를 지킨다. 현재 사용자 지시에 따라 커밋과 푸시는 사용자가 수행한다. 실패는 해당 단계에서 수정한다. 기능이나 검사를 삭제·비활성화해서 통과시키지 않는다.

이번 Phase 05 로컬 작업 브랜치는 feature/inventory-crafting이다. 전체 검증 전 main에 병합하지 않는다. 저장 형식 변경은 구형 원본 백업과 검증 가능한 이행 경로를 제공하고, 알 수 없는 콘텐츠 데이터를 가능한 한 보존한다. 새로운 외부 의존성은 꼭 필요한 경우에만 추가한다. 실제 실행하지 않은 검증과 구현하지 않은 기능을 완료로 표시하지 않는다.

## 단계별 상태와 검증 문턱

| 단계 | 상태 | 구현 범위 / 다음 단계 전 필수 검증 | 커밋 메시지 |
|---|---|---|---|
| 00 저장소 분석 | 완료 | [아키텍처](architecture.md), [실측 기준선](baseline/2026-11-04/README.md). Debug/Release 빌드, 기존 229개 검사 각각, Release 그래픽 7모드, CPU 메시/회로 벤치마크 통과 | test: establish engine baseline |
| 01 20 TPS 물리 | 완료 | [물리 계약](physics.md), [검증 기록](validation/phase01/README.md). Debug/Release 각각270개, OpenGL7모드 통과.  정확한 50ms GameClock → 기존 상수/가속도/중력/점프/마찰 검증·변환 → 프레임 입력 분리 → 위치 보간 → 프레임별 카메라 → 충돌/점프. 60/144/240 FPS에서 같은 틱 수·이동, 기존 조작/충돌 유지 | refactor: finalize 20 tps simulation |
| 02 Registry/Event | 완료 | [Registry API](registries.md), [검증](validation/phase02/README.md). Debug/Release 각각323개와 OpenGL7모드 통과.  Block byte 사용 전수 확인 → namespace:name 영구 ID → Registry<T>, Block/Item/EntityType Registry → 런타임 숫자 ID 분리 → 기존 콘텐츠 등록 → 설치/파괴/틱 이벤트 → 등록/동결 → 구형 ID 이행. 255개 초과 ID, 중복/누락/순서 변경, 구형 저장과 알 수 없는 콘텐츠 보존 검사. DLL 로더는 범위 밖 | feat: introduce content registries |
| 03 청크/저장 | 완료 | [저장 계약](storage.md), [검증](validation/phase03/README.md). Debug/Release 각각397개와 OpenGL7모드 통과.  16³ 유지 → 월드/청크 좌표와 음수 floor division → Unloaded/Loading/Generated/Meshing/Ready/Unloading 상태와 취소·실패·재시도 → 저장 Dirty → Registry 팔레트 → 메타데이터/청크 파일 분리 → 구형 이행 → 원자 교체/복구. 음수·경계·Dirty만 저장·실패 시 기존 데이터 보존 검사 | refactor: implement chunk persistence |
| 04 무한 스트리밍 | 완료 | [스트리밍](streaming.md), [낙하](falling-blocks.md), [검증](validation/phase04/README.md). Debug/Release 각각525개, OpenGL8모드, 실제120초/2400틱 이동·저장 복원 통과.  플레이어 반경/우선순위 큐 → 시드별 청크 생성과 경계 연속성 → 백그라운드 지형/CPU 메시 → 렌더 스레드 GPU 업로드 → 거리/언로드/Dirty 저장/자원 해제. 재진입 동일성, 취소된 작업의 수명 안전성, 작업 예산, 반복 이동/장시간 메모리 검사와 좌표/디스크 한계 문서화 | feat: implement infinite world streaming |
| 05 인벤토리/제작 | 완료 | [인벤토리 계약](inventory.md), [검증](validation/phase05/README.md). Debug/Release 각각588개, OpenGL9모드, 30회 저장·복원 및 실패/누락 모드 검사 통과.  ItemDefinition/ItemStack → 수량·스택 한계 → 36칸/9핫바 → 기존 수량 이행 → 이동/병합/분할 → 드래그/Shift클릭/Q → 2×2/3×3 → shaped/shapeless → JSON → UI → 결과/재료 소비. 저장 반복·실패·동작 조합에서 손실/복제 없음 검사 | feat: implement inventory and crafting |
| 06 조명/시간 | 대기 | Skylight/Blocklight 0–15 데이터 → 발광 속성 → 증분 추가/제거/청크 경계 전파 → 메시 연동 → 발광석/바다랜턴/횃불/램프 → 시간/태양/달/하늘/환경광 → 저장. 광원 제거·경계·로드 검사, 낮밤의 전역 밝기는 렌더 단계 적용으로 전체 remesh 방지 | feat: implement dynamic lighting |
| 07 유체 | 대기 | 상태/레벨 → 하강/수평/근원 규칙 → 예약 틱 → 물·용암 반응 → 투명 메시 → 수영/부력/저항/익사/용암 피해 → 경계 → 예약 상태 저장. 20 TPS 예약 셀 처리, 전 월드 매 틱 순회 금지, 경계/재진입/정합성/성능 검사 | feat: implement fluid simulation |
| 08 고급 회로 | 대기 | 행동 인터페이스 → 방향 연결 → 예약 틱 → 버튼/압력판/리피터/비교기/반전/관측기/피스톤/점착 피스톤 → 이동 블록의 회로·조명·유체 이벤트. 기존 레버/전선/램프, 지연·방향·블록 이동·순환·과부하 검사 | feat: expand circuit components |
| 09 엔티티 | 대기 | ID/타입 Registry → 상태/수명 → 위치/속도/충돌 → 20 TPS → 블록 충돌 → 체력/피해 → Item Entity → 렌더링 → 저장 → 청크 연동 → 스폰/디스폰. 생성·충돌·드롭 수량·저장·언로드 검사 | feat: introduce entity system |
| 10 몹/AI | 대기 | 상태 머신 Idle/Wander/Follow/Flee/Chase/Attack → 경로 탐색 → Pig/Cow/Sheep/Zombie → 스폰 규칙 → AI 예산. 행동 전이·우회/불가 경로·저장·청크 재진입·처리비용 검사 | feat: implement mobs and ai |
| 11 날씨 | 대기 | 바이옴/기온 → Clear/Rain/Thunderstorm/Snow → 전환 → 입자/환경음 → 안개/하늘/조명 → 번개 → 저장/스트리밍 연동. 상태 전환·바이옴 효과·저장/재진입 검사 | feat: introduce weather system |
| 12 통합/최적화 | 대기 | 새/구형 월드의 TPS, P95/P99, 생성 중 정지, 메모리/GC, GPU 수명, 대규모 회로/유체/AI/조명, 장시간 안정성과 저장 무결성 측정. 퇴행 원인 측정 후 개선; 실측 없이 최적화 성공 주장 금지 | perf: optimize integrated engine |
| 13 문서/배포 | 대기 | README → 시스템/API/저장 이행/20 TPS 규칙 문서 → Windows/Linux CI → Debug/Release → win-x64 self-contained → 실행 확인 → 변경 이력/알려진 문제. 배포본은 실제 실행 검증 후 완료 | docs: document major engine expansion |

## 사용자 추가 요청 — IP 멀티플레이

Phase05 뒤에 사용자 요청으로 IP 직접 접속을 추가했다. [멀티플레이 계약](multiplayer.md), [검증 기록](validation/multiplayer/README.md)을 따른다. F8 호스트/참가/복귀, 최대8명 참가, 상대 아바타·월드 청크·편집·회로·낙하·pickup 공유, 플레이어별 인벤토리/제작·재접속 저장을 구현했다. 현재 Debug/Release 각각651개, OpenGL10모드 통과. 실제 host+2client loopback 세션의 틱/수량/송수신/작업 예산을 측정했다. 커밋·푸시는 하지 않는다.

이 추가 작업은 Phase06 이후의 조명·유체·일반 엔티티 구현을 완료한 것으로 표시하지 않는다. 로비·전용 서버 서비스·NAT 중계·전문 치트 방지는 별도 범위다.

## 사용자 추가 요청 — 처음 실행·월드 생성 UX

공식 Minecraft 안내의 타이틀·Singleplayer·생성 흐름과 Game/World 탭 구성을 조사하고, 타이틀 → 월드 목록/생성 → 로딩 → 플레이 → 저장 후 타이틀 복귀를 구현했다. 이름·시드·Natural/Classic 선택, GUID별 독립 저장, 이름 변경·명시적 백업·확인 후 Trash, 기존 단일 저장 발견, 타이틀 직접 IP 접속을 지원한다. [시작 UX 계약](startup-ux.md), [검증 기록](validation/startup-ux/README.md)을 따른다. Debug/Release 각각707개, 실제 OpenGL11모드와 시작 동선 네이티브37항목이 통과했다. 영문 UI이며 Phase06 이후 게임 시스템을 완료한 것으로 표시하지 않는다.

## 이어서 할 작업

Phase 00/01은 기존 커밋으로 보존했고 Phase 02 Registry·이벤트와 Phase 03 청크 상태·저장의 검증을 완료했다. 현재 사용자 지시에 따라 변경은 git add까지만 수행하며 커밋·푸시는 하지 않는다. Phase 04의 스트리밍·백그라운드 메시·렌더 스레드 업로드·저장/언로드 검증과 추가 요청한 모래/붉은 모래/자갈 중력도 완료했다. Phase 05의 실제 슬롯·제작·마우스 조작·버리기/줍기·저장 이행과 사용자 추가 요청인 IP 멀티플레이도 완료했다. 다음 구현은 Phase 06 조명·낮밤이다. Phase 06–13은 미구현이며 전파 조명·유체·일반 엔티티/몹/날씨는 이번 작업에 포함하지 않는다. 각 단계 종료 보고에는 변경 파일, 빌드·검사 결과, 실제 성능/메모리 측정과 미검증 범위를 남긴다.

기준선은 CPU 작업과 기존 그래픽 경로를 확인한 것이다. Phase 04에서는 120초 스트리밍과 CPU·네이티브 스모크 P95/P99를 측정했다. 더 긴 통합 부하의 전체 프레임·GPU 메모리와 후속 시스템 성능은 Phase 12에서 새로 계측해야 한다. 원격 CI 실행과 배포본 실행은 아직 이번 변경의 결과로 확인하지 않았다.
