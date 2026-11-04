# Registry와 Event API

이 문서는 Phase 02에서 추가한 .NET 10 API와 v3 스냅샷을 설명한다. 엔티티·슬롯 인벤토리·제작·DLL 모드 로더의 완성을 의미하지 않는다.

## 콘텐츠 ID와 등록 수명

ContentId는 저장과 API 경계에서 사용하는 안정적인 문자열 식별자다. 형식은 namespace:path이며 소문자 ASCII 문자, 숫자, 밑줄, 하이픈, 점을 허용한다. path에는 슬래시도 허용한다. 콜론은 정확히 하나 필요하고 양쪽 값은 비어 있을 수 없다. 대소문자 자동 변환은 하지 않는다. 예: blockcircuit:oak_log, example:blue_stone.

Registry<T>는 Register(ContentId, value)로 등록하고 int 런타임 ID를 반환한다. Get, GetId, GetRuntimeId, TryGetRuntimeId로 값을 조회한다. 중복 ID, null 값, 초기화하지 않은 ID와 동결 후 등록은 거부한다. 런타임 ID는 등록 순서에 따라 달라지므로 파일에 영구 식별자로 기록하지 않는다. int 기반이며 byte의 255개 제한을 받지 않는다.

GameRegistries.Create(configure)의 순서는 다음과 같다.

1. 내장 블록 180개(설치 가능 178개)를 BlockRegistry에 등록한다. 공기/기반암을 제외한 블록 아이템은 Item Registry에도 등록한다.
2. configure 콜백에서 추가 블록·아이템·엔티티 타입을 등록한다.
3. Blocks, Items, EntityTypes를 모두 Freeze한다.
4. 동결된 GameRegistries를 World 생성자에 넘긴다. 생략하면 GameRegistries.Default를 사용한다.

내장 영구 ID는 기존 enum 이름을 snake_case로 바꾼 blockcircuit 네임스페이스다. 기존 숫자 0–179는 내장 호환성을 위해 보존한다. 내장 enum 이름을 바꾸면 영구 ID도 달라지므로 저장 호환성 변경으로 다뤄야 한다. 블록·아이템·엔티티 타입의 런타임 ID 공간은 서로 독립적이다.

~~~csharp
var registries = GameRegistries.Create(r =>
{
    r.Blocks.Register(new ContentId("example:blue_stone"),
        new BlockInfo(Block.Air, "Blue Stone", BlockCategory.Stone,
            0x4567AB, BlockPattern.Stone, TextureBlock: Block.Stone));
    r.Items.Register(new ContentId("example:blue_stone"),
        new ItemDefinition("Blue Stone", MaxStack: 64));
    r.EntityTypes.Register(new ContentId("example:marker"),
        new EntityTypeDefinition("Marker"));
});
var world = new World(generate: false, registries: registries);
var block = registries.Blocks.GetBlock(new ContentId("example:blue_stone"));
world.Place(new Cell(5, 5, 5), block);
~~~

BlockRegistry는 등록한 BlockInfo.Id를 실제 런타임 ID로 설정한다. 예제의 Block.Air는 입력 레코드의 임시 값이며 등록 결과가 공기가 되는 것은 아니다. TextureBlock은 내장 블록의 atlas 텍스처를 재사용하는 fallback이고 유효한 내장 ID여야 한다. 확장 블록의 메타데이터는 world.Registries.Blocks.GetInfo(block)로 조회한다. BlockCatalog는 내장 콘텐츠 호환 경계이므로 다른 월드의 확장 ID 조회에 사용하지 않는다.

블록 등록이 대응 아이템까지 자동 생성하지는 않는다. 필요한 아이템을 같은 문자열 ID로 별도 등록한다. ItemDefinition(Name, MaxStack)은 등록 데이터 경계다. 현재 수량 저장은 여전히 블록별 Inventory 배열이며 슬롯·스택 소비·제작은 Phase 05 범위다. EntityTypeDefinition(Name)과 EntityTypes Registry는 타입 등록 경계만 제공하고 엔티티 인스턴스·스폰·AI·저장을 구현하지 않는다.

등록은 단일 스레드에서 수행한다. 동결 후 Registry의 조회는 동시 읽기가 가능하지만 등록한 객체 자체의 내부 가변 상태까지 동기화해 주지는 않는다. 월드·이벤트·편집·저장은 시뮬레이션 소유 스레드에서 사용한다. GPU API는 계속 렌더링 스레드에서만 호출한다. DLL 검색·로드·격리 기능은 추가하지 않았다.

## 이벤트 구독과 실행 시점

각 World는 독립적인 Events(EventHub)를 소유한다. Subscribe<T>(Action<T>)는 해제용 IDisposable을 반환한다. 구독 순서대로 현재 구독 목록의 스냅샷을 호출한다. 콜백 도중 구독/해제한 결과는 다음 Publish부터 적용한다. 같은 핸들러를 여러 번 등록하면 각 구독을 별도로 호출·해제한다.

~~~csharp
using var subscription = world.Events.Subscribe<BlockPlacedEvent>(placed =>
{
    Console.WriteLine(placed.World.GetPersistentId(placed.Cell));
});
~~~

| 이벤트 | 발행 조건과 시점 |
|---|---|
| BlockChangedEvent(World, Cell, Previous, Current) | 블록 런타임 값의 실제 변경. Place/Mine/이력 재생 내부에서는 편집 처리가 끝난 뒤 발행한다. 트랜잭션 밖 저수준 Set은 즉시 발행한다. |
| BlockPlacedEvent(World, Cell, Block) | 성공한 공개 Place 작업 후. 수량 차감·편집 이력·회로 처리가 반영된 상태다. 실패한 설치에는 발행하지 않는다. |
| BlockBrokenEvent(World, Cell, Block) | 성공한 공개 Mine 작업 후. 수량 회수와 지원되지 않는 부품 정리를 반영한 상태다. 실패한 채굴에는 발행하지 않는다. |
| GameTickEvent(World, Tick) | World.Tick이 TickCount를 증가시킨 뒤 발행한다. 실제 게임의 고정 20 TPS 경로에서 호출한다. |

이벤트는 사후 알림이며 취소 가능한 사전 훅이 아니다. BlockChanged 알림 지연은 편집 트랜잭션을 마친 뒤 발행한다는 뜻이며 콜백 실행 자체를 작업 큐나 다른 스레드로 보내지는 않는다. 저수준 Set 또는 실행 취소/재실행을 공개 Place/Mine과 동일한 사용자 액션 이벤트로 간주하지 않는다. 레버 상태만 바뀌면 블록 타입 변경 이벤트도 발생하지 않는다. 회로의 내부 BlockEvent 큐는 별도이며 기존 회로 계산을 유지한다.

구독자 예외는 이후 구독자 호출이나 이미 성공한 월드 편집을 중단하지 않는다. EventHub.Errors에는 최근 최대 128개 예외를 남긴다. 호출자가 이를 확인해야 하며 자동 로깅/재시도는 없다. 이벤트 콜백의 월드 편집은 다시 이벤트를 발생시킬 수 있으므로 재귀적·무한 편집 루프를 만들지 않도록 한다. EventHub 자체는 다중 스레드 구독/발행을 보장하지 않는다.

## 저장 ID와 v3 스냅샷

ExportSnapshot은 v3 단일 JSON을 쓴다. 런타임 숫자 대신 문자열 팔레트를 사용하므로 확장 콘텐츠 등록 순서가 바뀌어도 같은 블록을 복원한다. 기존 40×28×40 Cells와 선택적 ExtraCells로 영역 밖 비공기 셀도 보존한다. Phase 03부터 기본 World.Save는 [v4 매니페스트와 청크 파일](storage.md)을 기록하며, 이 절은 명시적 v3 내보내기 형식을 설명한다.

| 필드 | v3 계약 |
|---|---|
| Version | 3 |
| Palette | 중복 없는 영구 ID 문자열 배열. 실제 사용 블록을 기록한다. |
| Cells | 44,800개의 int 팔레트 인덱스. 순서는 (y * 40 + z) * 40 + x. |
| Counts | 영구 ID → 비음수 수량. 런타임 배열 순서와 분리한다. |
| BlockData | 기존 영역 내 셀 좌표와 불투명 JSON payload. 빈 배열도 허용한다. |
| ExtraCells | 선택적인 영역 밖 비공기 셀 배열. 월드 좌표, 영구 ID, JSON, 켜진 레버 상태를 기록한다. 누락 시 빈 배열. |
| Switches | 켜진 레버 셀. 회로 전원은 로드 후 재계산한다. |
| Position / Yaw / Pitch | 기존 플레이어/카메라 필드. 유한성과 월드 경계를 검증한다. |
| Seed / TerrainVersion / TickCount | 생성 메타데이터와 경과 틱. TickCount는 비음수다. |

로드는 팔레트 ID·중복·인덱스·셀 수·수량·payload 위치·레버 위치·메타데이터를 검증하고 새 청크/수량/부가 데이터를 준비한 뒤 활성 상태를 교체한다. 잘못된 데이터로 활성 월드를 부분 교체하지 않는다. v3/v4의 Counts에 없는 현재 등록 블록 수량은 0이다.

World.Load는 v1/v2/v3/v4를 읽는다. 기존 숫자 블록 ID와 수량, 레버, 생성 정보/플레이어를 유지하고 기존 v1에 없던 콘텐츠에는 초기 수량을 채운다. 기본 저장은 v4이며 SaveLegacy는 호환성 도구용 v2 내보내기다. 확장 Registry·셀 payload·알 수 없는 수량·영역 밖 셀을 v2로 표현할 수 없으면 SaveLegacy를 거부한다. World.Blocks의 byte 스냅샷도 구형 호환용이며 255를 초과하는 실제 블록이 있으면 checked 변환으로 거부한다. 일반 저장에 이 스냅샷을 사용하지 않는다.

## 누락된 콘텐츠와 부가 데이터

알 수 없는 팔레트 ID는 화면/충돌에서 Stone으로 대체하면서 원래 ContentId를 셀별로 보존한다. GetPersistentId(cell)은 원래 ID를 반환한다. 알 수 없는 Counts는 UnknownInventory에 별도로 남긴다. GetBlockData/SetBlockData는 셀 JSON 데이터를 조회/설정하고 저장 경계를 넘을 수 있도록 JsonElement를 Clone한다. 공기 셀이나 Undefined JSON은 설정할 수 없다.

등록이 없는 상태에서 다시 저장해도 원래 ID·payload·수량을 유지한다. 대응 콘텐츠를 다시 등록해 로드하면 그 타입으로 복원한다. 누락 블록은 일반 Mine으로 채굴할 수 없으므로 fallback Stone을 회수하며 원본을 잃는 동작을 막는다. 의도적인 저수준 Set 교체는 셀의 누락 ID와 payload를 제거하므로 복구를 원하면 먼저 백업한다. 자동 복원은 payload의 의미를 해석하거나 해당 모드의 논리를 실행한다는 뜻이 아니다.

## 기존 파일 백업과 교체

v3를 기존 v1/v2 파일 경로에 저장할 때 기존 파일을 먼저 같은 디렉터리의 backups/<이름>-legacy-v<버전>-<GUID>.json으로 복사한다. 백업을 만들 수 없으면 새 저장으로 진행하지 않는다. Load 자체는 파일을 자동 변환해 덮어쓰지 않는다.

명시적 v3 ExportSnapshot은 같은 디렉터리의 고유 .tmp-<GUID> 파일에 UTF-8로 쓰고 Flush(true)한 뒤 기존 파일에는 File.Replace, 새 파일에는 File.Move를 사용한다. 실패/완료 후 임시 파일을 정리한다. 지원되지 않는 교체나 I/O 실패는 호출자에게 전달하며 기존 정상 파일을 직접 부분 덮어쓰지 않는다. 이 경로는 저장 실패 안전성을 위한 것이며 모든 파일시스템에서 전원 장애 내구성을 보장하거나 디렉터리를 fsync하는 것은 아니다. 동시 저장 호출은 지원하지 않는다. 일반 v3→v3 저장마다 이행 백업을 추가하지는 않는다. 구형 SaveLegacy의 내보내기 경로는 이 신형 저장 경로와 구분한다.

기본 블록의 영구 이름은 Content/LegacyBlockIds.cs의 명시적 표에 고정한다. CLR enum 이름 변경으로 저장 ID가 달라지지 않는다.
