# 청크 저장 형식과 수명

Phase 03의 저장 API는 .NET 10, 16×16×16 청크와 [Registry 영구 ID](registries.md)를 사용한다. World.Save는 v4 매니페스트와 청크 파일을 기록하고 World.Load는 v1–v4를 읽는다. ExportSnapshot은 단일 파일 v3 내보내기로 남긴다.

## 파일 구성과 소유권

예를 들어 저장 경로가 world.json이면 다음을 하나의 저장 묶음으로 다룬다.

~~~text
world.json                      현재 v4 월드 매니페스트
world.json.bak                  이전 매니페스트, 복구 후보
world.json.chunks/
  -1_0_0-<32자리 GUID>.json      변경 불가능한 청크 세대 파일
  0_0_0-<32자리 GUID>.json
world.json.lock                 저장 중 배타적으로 여는 잠금 파일
backups/
  world-legacy-v2-<GUID>.json    구형 파일 이행 전 보존본
~~~

청크 파일은 새 이름으로 작성하며 기존 세대 파일을 덮어쓰지 않는다. 매니페스트가 좌표별 사용할 파일명과 SHA-256을 지정한다. .bak도 같은 .chunks 디렉터리의 파일을 참조한다. 복사·이동·백업 시 매니페스트만 옮기지 말고 해당 .chunks 디렉터리와 .bak를 함께 보관한다. 파일 이름만 바꾸면 참조 디렉터리 이름도 일치하도록 옮겨야 한다. 새 월드 생성의 세션 백업도 그 저장 경로의 별도 청크 디렉터리를 가질 수 있다.

ChunkSaveStore.Save(world, path, position, yaw, pitch, checkpoint?)가 실제 저장을 수행한다. World.LastSaveReport는 WrittenChunks/ReusedChunks/RemovedChunks/MigratedLegacy를 제공한다. 저장 위치나 매니페스트 Generation이 바뀌면 이전 파일 재사용을 하지 않는다. 같은 경로의 동시 저장은 .lock 파일의 FileShare.None 잠금으로 거부한다. 잠금 파일이 디스크에 존재하는 것 자체는 사용 중이라는 뜻이 아니다.

## v4 매니페스트와 청크 JSON

| 매니페스트 필드 | 의미 |
|---|---|
| Version / Generation | 4와 저장 세대 GUID |
| Seed / TerrainVersion / TickCount | 월드 생성 정보와 비음수 경과 틱 |
| Position / Yaw / Pitch | 플레이어·카메라 상태 |
| Counts | 영구 ID별 비음수 수량. 알 수 없는 ID도 보존 |
| Chunks | 중복 없는 좌표별 File, Sha256, SwitchSignature 참조 |

| 청크 필드 | 의미 |
|---|---|
| Version / Coord | 청크 형식 1과 매니페스트에 일치하는 청크 좌표 |
| Palette | 중복 없는 namespace:path 문자열 배열, 1–4,096개 |
| Cells | 길이 4,096인 int 팔레트 인덱스 배열 |
| Switches | 해당 청크 안에서 켜진 실제 레버 셀 |
| BlockData | 해당 청크의 비공기 셀별 불투명 JSON 데이터 |

청크 배열 인덱스는 (localY * 16 + localZ) * 16 + localX다. Switches/BlockData의 셀 좌표는 로컬 값이 아닌 월드 좌표다. SwitchSignature는 정렬한 활성 레버 위치 목록이며 공개 Switches 컬렉션을 직접 바꾼 경우에도 변경을 감지한다. 회로 Power는 저장하지 않고 로드 후 재계산한다.

로드는 모든 참조 파일의 SHA-256, 좌표·이름 일치, 배열 길이/인덱스, ID 중복, 수량, 레버와 payload 위치를 검증한다. 파일명은 좌표와 GUID로 제한하여 매니페스트가 임의 경로를 지정하지 못하게 한다. 메타데이터 읽기는 64 MiB, 개별 청크는 32 MiB, 매니페스트 청크 참조 수는 1,000,000개로 제한한다. 제한은 실제 운영 용량을 보장하는 목표치가 아니다.

알 수 없는 블록은 Stone으로 표시/충돌하면서 원래 영구 ID와 JSON을 보존한다. 누락된 수량도 별도로 보존하고 재등록 후 복원한다. v3/v4의 Counts에서 빠진 현재 등록 ID의 수량은 0이다. v1의 추가 콘텐츠 초기 지급은 기존 마이그레이션 규칙을 유지한다.

## 좌표와 현재 게임 범위

ChunkCoord.FromWorld는 음수에도 floor division을 사용하고 ToLocal은 0–15를 반환한다. 예를 들어 X=-1은 청크 -1/로컬 15, X=-16은 청크 -1/로컬 0, X=-17은 청크 -2/로컬 15다. 청크당 축 좌표 범위는 -134,217,728–134,217,727이며 Origin 곱셈은 checked다. 월드 셀은 32비트 정수이므로 청크의 16개 로컬 셀 모두 정수 범위 안에 들어간다.

World.Get/저수준 Set과 청크 저장은 음수·기존 영역 밖 셀을 다룬다. 현재 지형 생성, 공개 플레이어 Place와 플레이어 충돌/카메라 경계는 기존 40×28×40 플레이 영역을 유지한다. Phase 03은 무한 스트리밍을 구현하지 않는다. 좌표가 정수 범위 안이어도 float 기반 렌더링의 먼 거리 정밀도, 실제 메모리·디스크 제한은 별도다.

## 청크 수명과 작업 토큰

ChunkLifecycle은 Unloaded → Loading → Generated → Meshing → Ready → Unloading 상태를 정의한다. 일반 resident Chunk는 Generated에서 시작하고, 로드/메시/해제 작업의 Begin 메서드는 그 작업만의 증가하는 토큰을 반환한다. CompleteLoading/CompleteMeshing/CompleteUnloading은 현재 활성 토큰과 예상 상태가 일치할 때만 결과를 반영한다.

- Cancel은 작업 전 안정 상태로 되돌리고 이전 토큰을 무효화한다. 로드 취소는 Unloaded, 메시 취소는 재시도 가능한 Generated, 해제 취소는 이전 Generated/Ready로 돌아간다.
- Fail은 같은 취소 경로를 거쳐 LastError에 실패를 기록한다. 새 작업을 시작하거나 정상 완료하면 오류를 초기화한다.
- Invalidate는 변경된 resident 청크를 Generated로 돌리고 진행 중 메시 토큰도 무효화한다. Unloaded/Loading 상태에서의 무효화는 거부한다.
- 제거된 청크의 Retire는 진행 중 작업을 취소하고 Unloading → Unloaded를 완료한다. 늦게 도착한 이전 완료 토큰은 거부한다.

BlockRenderer는 실제 메시 생성/업로드를 BeginMeshing/CompleteMeshing으로 감싸고 실패 시 Fail을 호출한다. CPU 청크 상태 Unloaded와 GPU 버퍼 해제 시점은 별개다. GPU 캐시는 렌더링 스레드가 청크 제거/저장소 교체를 감지하거나 렌더러를 종료할 때 해제한다. 현재 동기 저장 로드는 검증된 resident 청크를 생성하며 비동기 로딩 작업 큐와 원거리 언로드는 Phase 04 범위다.

## 메시 변경과 저장 Dirty의 분리

Chunk.Revision은 메시 무효화용이며 이웃 면 변경이나 회로 외형 변화에도 증가한다. Chunk.ContentRevision/IsDirty와 ChunkManager.DirtyCoordinates는 영속 데이터 변경을 추적한다. 단순한 면 이웃 무효화는 그 이웃 청크를 디스크에 다시 쓰게 만들지 않는다.

- 블록 실제 변경, 셀 JSON, 레버 상태는 해당 콘텐츠를 Dirty로 표시한다.
- 같은 블록의 no-op 쓰기나 화면용 메시 갱신만으로 콘텐츠 세대를 올리지 않는다.
- 마지막 블록/전체 청크 제거도 좌표별 Dirty 기록을 남겨 이전 저장 참조에서 제거할 수 있다.
- MarkSaved(coord, capturedRevision)은 현재 콘텐츠 세대가 일치할 때만 Dirty를 해제한다. 저장 도중 이후 편집이나 같은 좌표의 청크 재생성을 이전 완료 통지로 지우지 않는다.
- 검증한 로드를 설치한 뒤 MarkAllSaved로 읽은 상태를 clean으로 표시한다.

저장은 이전 매니페스트가 참조한 모든 청크를 크기와 SHA-256으로 먼저 검증하여 정상 백업을 교체해도 되는지 판단한다. 이 검증은 현재 Dirty인 청크에도 적용한다. clean 파일은 이 검증 결과로 재사용한다. 검증에 실패하면 메모리의 현재 청크로 새 파일을 쓰고 정상 복구 매니페스트를 보호한다. 변경된 청크만 새 파일로 쓴다. Counts/플레이어/시간 등 매니페스트 메타데이터는 매 저장마다 기록한다. 다른 경로로 저장하거나 다른 세대가 발견되면 현재 상태 전체를 다시 기록한다. 저수준 저장 API와 월드 변경은 동일 소유 스레드에서 사용하며 동시 가변 월드 스냅샷을 보장하는 기능은 아니다.

## 커밋 순서와 실패 처리

1. 플레이어/저장 메타데이터를 확인하고 저장 잠금을 획득한다.
2. 현재 청크별 콘텐츠 세대와 상태를 포착한다. 재사용할 clean 파일을 선택하고 변경된 청크는 새 고유 파일에 쓴다.
3. 각 새 파일을 UTF-8로 쓰고 Flush(true) 후 교체/이동한다. 파일의 SHA-256을 새 매니페스트에 기록한다.
4. v1/v2/v3 경로를 이행한다면 원본 파일을 backups에 복사한다. 백업 실패는 새 매니페스트 커밋을 중단한다.
5. 새 매니페스트를 고유 임시 파일로 쓰고 기존 경로에는 File.Replace, 처음 저장하는 경로에는 File.Move로 커밋한다. 정상 이전 매니페스트는 .bak에 보관한다.
6. 성공한 세대만 Dirty 완료 처리하고 LastSaveReport를 갱신한다. 현재 및 .bak에 필요한 청크 파일을 유지하며 더 이상 참조하지 않는 고유 파일은 정리한다.

매니페스트 커밋 전 실패하면 이번 저장에서 만든 청크 파일을 정리하고 이전 저장 참조를 유지한다. 정리 오류로 이미 성공한 저장을 실패로 바꾸지는 않는다. 강제 종료로 남은 고아 청크는 이후 성공한 저장의 정리 대상이 될 수 있다. 현재/.bak가 참조하는 청크는 정리하지 않는다.

SaveCheckpoint.ChunksWritten과 BeforeManifestCommit 콜백은 결정적 I/O 실패를 주입하는 검사 경계다. 콜백 예외는 커밋 실패 경로를 실행한다. 이 콜백은 모드의 일반 월드 편집 이벤트나 백그라운드 저장 API가 아니다.

파일 데이터 Flush와 원자적 이름 교체를 사용하지만 모든 파일시스템의 전원 장애 내구성이나 디렉터리 fsync를 보장하지는 않는다. 청크 쓰기/매니페스트 교체는 동기 I/O다. 비동기 작업과 프레임별 작업 예산은 Phase 04에서 다룬다.

## 복구와 구형 파일 이행

World.Load는 현재 매니페스트와 모든 청크를 별도 임시 상태로 검증한다. 실패하면 path.bak를 같은 저장의 청크 디렉터리와 함께 검증한다. 성공한 상태만 활성 월드로 설치하며 둘 다 실패하면 기존 활성 월드를 유지하고 InvalidDataException으로 원인들을 전달한다. LastLoadRecovered는 .bak 사용 여부를 알려 준다. 복구 읽기는 원본 파일을 즉시 덮어쓰지 않는다. 복구 후 다음 저장은 손상된 현재 매니페스트로 정상 .bak를 덮어쓰지 않으며 새 청크 세대를 기록한다. 현재 매니페스트 JSON이 정상이지만 그 청크가 손상된 경우에도 같은 보호를 적용한다.

v1/v2/v3 로드도 계속 지원한다. 다음 World.Save는 v4로 기록하고 구형 원본을 보존한다. ExportSnapshot의 v3 단일 파일은 기존 영역의 Cells와, 선택적인 ExtraCells 배열로 그 밖의 비공기 셀/ID/JSON/레버 상태를 내보낸다. SaveLegacy v2는 확장 ID·payload·알 수 없는 수량 또는 영역 밖 셀이 있으면 내보내기를 거부한다.

실행 검증과 실제 성능/메모리 결과는 [Phase 03 검증 기록](validation/phase03/README.md)과 로드맵 상태를 따른다. 이 문서만으로 테스트 통과나 Phase 04 기능 완료를 주장하지 않는다.

32 MiB 청크·64 MiB 매니페스트/스냅샷 한계를 저장과 읽기에서 함께 검사한다. 청크 디렉터리/파일의 심볼릭 링크·재분석 지점은 지원하지 않는다. 저수준 ChunkManager.Set/Remove도 월드에 연결된 블록 데이터와 레버 상태를 무효화하므로 삭제 후 청크를 재생성해도 오래된 모드 데이터가 복원되지 않는다.
