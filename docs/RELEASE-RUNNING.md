# BlockCircuit 실행 파일

`client`는 게임 클라이언트, `server`는 창 없는 전용 서버입니다.
각각 `linux-x64` / `win-x64`, JIT 실행 파일을 선택하세요.
JIT 버전은 .NET 런타임을 포함한 단일 실행 파일입니다. AOT 버전은 제공하지 않습니다.
.NET 별도 설치는 필요 없습니다. SHA-256 파일로 다운로드를 확인할 수 있습니다.

- Windows: `.exe` 실행. 함께 배포된 네이티브 DLL이 있으면 같은 폴더에 놓으세요. `vcruntime140.dll`도 제공합니다.
- Linux: `chmod +x BlockCircuit-*linux-x64-*` 후 실행. 네이티브 `.so`가 있으면 같은 폴더에 놓으세요.
- Linux 클라이언트는 X11/OpenGL 및 `libgl1 libx11-6 libxrandr2 libxinerama1 libxcursor1 libxi6`가 필요합니다.
- JIT 파일은 네이티브 라이브러리를 임시 폴더에 추출합니다. 쓰기 가능한 임시 폴더가 필요합니다.
- 기본 제작법은 실행 파일에 내장됩니다. `Assets/recipes.json`이 있으면 이를 우선 사용하며, 접속자와 서버의 제작법은 일치해야 합니다.

## 서버

```sh
export BLOCKCIRCUIT_SERVER_PASSWORD='choose-a-password'
./BlockCircuit-server-linux-x64-jit --bind 0.0.0.0 --port 25565 --world ./data/world.json --seed 2026
```

기본 바인딩은 `127.0.0.1`입니다. 외부 접속을 허용할 때 `--bind 0.0.0.0`을 지정하고 TCP 포트 25565를 여세요.
Windows는 PowerShell에서 `$env:BLOCKCIRCUIT_SERVER_PASSWORD='...'`로 설정합니다.
최대 참가자 8명, 20 TPS이며 실행 중 60초마다 저장하고 Ctrl+C / Linux SIGTERM 종료 시 최종 저장합니다.
저장 경로의 JSON뿐 아니라 `.chunks` 및 관련 파일을 포함한 데이터 폴더 전체를 보존·백업하세요.
동일한 월드를 게임과 서버에서 동시에 열지 마세요. 저장 실패는 오류 종료로 처리됩니다.
강제 종료(SIGKILL) 시 마지막 저장 이후의 변경은 손실될 수 있습니다.

현재 프로토콜은 암호화와 계정 인증을 제공하지 않습니다. 인터넷 사용 시 VPN 등 신뢰할 수 있는 연결을 사용하세요.
`--help`로 옵션을, `--run-seconds 2`로 유한 실행을 확인할 수 있습니다.

## Actions

main push / 수동 실행: 회귀 테스트 → 4종 실행 파일 빌드 → 배포 파일 자체 검사 → GitHub Release.
main 대상 PR: 동일한 검사와 빌드, Release 발행 제외.
한 가지 빌드라도 실패하면 Release를 발행하지 않습니다. Release에는 ZIP 없이 실행 파일·네이티브 의존 파일·라이선스·체크섬을 직접 올립니다.
Actions 내부 전달에는 GitHub artifact를 사용합니다.
