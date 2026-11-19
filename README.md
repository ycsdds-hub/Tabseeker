# Tabseeker

탭 탐색기(QTTabBar 같은)와 전체 드라이브 즉시 검색(Everything 같은)을 한 창에 합친 Windows 파일 탐색 도구.

[TabSeek](https://github.com/ycsdds-hub/TabSeek) 1.0.0을 바탕으로 새 이름으로 이어서 기능을 더해 가는 프로젝트입니다. 설정과 색인은 TabSeek와 따로 `%LOCALAPPDATA%\Tabseeker\` 에 저장되므로 두 앱을 함께 둘 수 있습니다.

- **탭**: 폴더마다 탭, 가운데 클릭으로 새 탭, 끌어서 순서 변경, 탭에 파일을 끌어 놓으면 그 폴더로 복사/이동, 닫은 탭 다시 열기, 종료 시 탭 복원
- **검색**: 위쪽 검색창에 입력하는 즉시 모든 NTFS 드라이브에서 파일/폴더 이름을 찾음. 1,000만 개 기준 대부분 0.05~0.4초
- **색인**: Everything처럼 NTFS MFT를 직접 읽어서 1,000만 개를 1~2분 안에 색인 (관리자 권한 도우미를 UAC로 한 번 띄움). 실행 중에는 파일 변경을 1~2초 안에 실시간 반영
- **어디서나 검색**: `Ctrl+Alt+F` 로 창을 불러와 검색창에 바로 입력
- **호버 메모**: 파일/폴더 이름에 마우스를 잠시 올리면 이름 바로 옆에 메모 말풍선이 뜸. 열어 보지 않고도 "이게 뭔지" 적어 두고 볼 수 있음 (아래 [메모](#메모))

## 메모

- **보기/쓰기**: 이름 글자 위에 마우스를 올리면 말풍선이 이름 옆에 붙어서 뜸 (메모가 있으면 0.45초, 없으면 0.9초 뒤). 그 안에 바로 입력하면 0.6초 뒤 자동 저장. 내용을 모두 지우거나 🗑 를 누르면 메모 삭제
- **크기**: 말풍선 오른쪽 아래 모서리를 끌어 조절. 메모마다 크기를 기억
- **붙어 있기**: 화살표가 항상 그 이름을 가리킴. 목록을 스크롤하거나 창을 옮기면 따라가고, 이름이 화면 밖으로 나가면 닫힘. 오른쪽에 자리가 없으면 이름 왼쪽에 뜸
- **표시**: 메모가 있는 이름 옆에 노란 노트 표시
- **키보드**: 선택한 항목에서 `Ctrl+M` (오른쪽 클릭 메뉴 ▸ 메모 쓰기), `Esc` 로 닫기
- **찾기**: 검색창에 `memo: 단어` → 메모 내용·경로로 찾기, `memo:` 만 쓰면 전체 메모 (설정 ▸ 메모 모아 보기)
- **파일을 옮겨도 유지**: 메모는 파일의 NTFS 고유 번호로 연결되어, Tabseeker 밖에서 이름을 바꾸거나 같은 드라이브 안에서 옮겨도 따라감. Tabseeker 안에서 폴더 이름을 바꾸면 안의 파일 메모도 함께 따라감
- **설정 ▸ 메모 없는 이름에도 호버 말풍선**: 끄면 메모가 있는 이름에만 말풍선이 뜸 (새 메모는 `Ctrl+M`)

### 메모 데이터베이스 보안

- 위치: `%LOCALAPPDATA%\Tabseeker\memo\memos.tsdb` (+ 직전 버전 백업 `memos.tsdb.bak`)
- 폴더는 숨김이고, **현재 Windows 사용자만** 열 수 있도록 접근 권한을 제한
- 파일 전체를 Windows DPAPI(현재 사용자 계정)와 Tabseeker 전용 비밀값으로 **암호화** — 메모 내용과 경로가 파일에 평문으로 남지 않음
- 메모를 만들고·읽고·고치고·지우는 코드는 Tabseeker 안에만 있음. 다른 프로그램이 파일을 고치면 암호 검증에 실패하므로, Tabseeker는 그 파일을 쓰지 않고 따로 보관(`*.damaged-날짜`)한 뒤 백업으로 되돌리고 알려 줌
- 한계: 같은 Windows 사용자 권한을 가진 프로그램이 파일을 **지우는 것** 자체는 막을 수 없음 (백업이 있어 하나만 지워지면 복구됨)

## 사용법

| 동작 | 키 / 마우스 |
|---|---|
| 새 탭 / 닫기 / 닫은 탭 다시 열기 | `Ctrl+T` / `Ctrl+W`·가운데 클릭 / `Ctrl+Shift+T` |
| 탭 이동 | `Ctrl+Tab`, `Ctrl+1~9`, 탭 바에서 마우스 휠 |
| 탭 복제 | 탭 더블클릭 |
| 폴더를 새 탭에서 열기 | 가운데 클릭, `Ctrl+Enter`, `Ctrl+더블클릭` |
| 뒤로 / 앞으로 / 위로 | `Alt+←` / `Alt+→` / `Backspace`·`Alt+↑`·빈 곳 더블클릭, 마우스 뒤로·앞으로 버튼 |
| 경로 입력 / 이 폴더 거르기 | `Ctrl+L` / `Ctrl+F` |
| 전체 검색창 | `Ctrl+E` (창 안), `Ctrl+Alt+F` (어디서나) |
| 이름 바꾸기 / 휴지통 / 새 폴더 | `F2` / `Delete` (`Shift+Delete` 영구 삭제) / `Ctrl+Shift+N` |
| 복사·잘라내기·붙여넣기 / 경로 복사 | `Ctrl+C`·`Ctrl+X`·`Ctrl+V` / `Ctrl+Shift+C` |
| 즐겨찾기 추가 | `Ctrl+D`, 폴더를 왼쪽 즐겨찾기로 끌어 놓기 |

### 검색 문법

| 입력 | 뜻 |
|---|---|
| `보고서 2026` | 이름에 두 단어 모두 포함 (AND) |
| `jpg\|png` | 둘 중 하나 (OR) |
| `!temp` | 제외 |
| `"연간 보고서"` | 띄어쓰기 포함 구절 |
| `*.psd`, `IMG_????.jpg` | 와일드카드 (이름 전체와 비교) |
| `ext:pdf;hwp` | 확장자 |
| `file:` / `folder:` | 파일만 / 폴더만 |
| `projects\src` | `\` 나 `/` 가 들어가면 전체 경로에서 찾기 |
| `regex:^IMG_\d+` | 정규식 |

결과에서 `Enter` = 열기, `Ctrl+Enter` = 들어있는 폴더를 새 탭으로, 가운데 클릭 = 폴더를 뒤쪽 새 탭으로.

## 색인 동작

1. 시작하면 `%LOCALAPPDATA%\Tabseeker\index.bin` 캐시를 바로 불러옴 (1,000만 개 약 2초)
2. 캐시가 없거나 6시간보다 오래됐으면 `Tabseeker.exe --build-index` 도우미를 관리자 권한(UAC)으로 실행해 MFT를 읽음
3. UAC를 거절하면 폴더를 하나씩 읽는 일반 모드로 대신 색인 (큰 하드디스크에서는 매우 느림)
4. 실행 중에는 FileSystemWatcher로 모든 NTFS 드라이브의 변경을 실시간 반영, 10분마다·종료할 때 캐시 저장
5. 설정(⚙) ▸ 색인 다시 만들기 로 언제든 새로 만들 수 있음

FAT32/exFAT 드라이브, Google Drive 같은 가상 드라이브는 색인하지 않음 (탐색은 가능).

메모리: 1,000만 항목에 약 1.2GB.

## 빌드

```
dotnet test
dotnet publish src/Tabseeker -c Release -o publish
```

`publish/Tabseeker.exe` 하나만 있으면 실행됨 (.NET 포함 단일 파일).

UI 스모크 테스트 (앱을 직접 띄우고 닫음): `powershell -File tests/ui/smoke.ps1`

## 구조

- `src/Tabseeker.Core` — 색인(`FileIndex`), MFT 읽기(`MftReader`), 검색 문법(`SearchQuery`), 메모 DB(`MemoStore`), 파일 고유 번호(`FileIdentity`)
- `src/Tabseeker` — WPF 앱: 탭(`MainWindow`), 폴더 탭(`FolderView`), 검색 탭(`SearchView`), 색인 서비스(`IndexService`), 메모 말풍선(`MemoBubble`, `MemoController`)
- `tests/Tabseeker.Core.Tests` — xUnit 테스트
- `tests/ui/smoke.ps1` — UI Automation 스모크 테스트
- `tools/make-icon.ps1` — 아이콘 생성

## 변경 기록

- Tabseeker 1.1.0 (2026-11-20): 호버 메모 말풍선, 암호화된 메모 데이터베이스, `memo:` 검색
- Tabseeker 1.0.0 (2026-11-20): TabSeek 1.0.0에서 이름을 Tabseeker로 바꿔 새로 시작 (실행 파일, 프로젝트, 네임스페이스, 설정 폴더)
- TabSeek 1.0.0 (2026-11-19): 첫 버전. 탭 탐색기, MFT 색인, 즉시 검색, 실시간 반영

