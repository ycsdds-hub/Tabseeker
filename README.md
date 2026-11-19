# Tabseeker

탭 탐색기(QTTabBar 같은)와 전체 드라이브 즉시 검색(Everything 같은)을 한 창에 합친 Windows 파일 탐색 도구.

[TabSeek](https://github.com/ycsdds-hub/TabSeek) 1.0.0을 바탕으로 새 이름으로 이어서 기능을 더해 가는 프로젝트입니다. 설정과 색인은 TabSeek와 따로 `%LOCALAPPDATA%\Tabseeker\` 에 저장되므로 두 앱을 함께 둘 수 있습니다.

- **탭**: 폴더마다 탭, 가운데 클릭으로 새 탭, 끌어서 순서 변경, 탭에 파일을 끌어 놓으면 그 폴더로 복사/이동, 닫은 탭 다시 열기, 종료 시 탭 복원
- **검색**: 위쪽 검색창에 입력하는 즉시 모든 NTFS 드라이브에서 파일/폴더 이름을 찾음. 1,000만 개 기준 대부분 0.05~0.4초
- **색인**: Everything처럼 NTFS MFT를 직접 읽어서 1,000만 개를 1~2분 안에 색인 (관리자 권한 도우미를 UAC로 한 번 띄움). 실행 중에는 파일 변경을 1~2초 안에 실시간 반영
- **어디서나 검색**: `Ctrl+Alt+F` 로 창을 불러와 검색창에 바로 입력

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

UI 스모크 테스트 (앱을 띄운 상태에서): `powershell -File tests/ui/smoke.ps1`

## 구조

- `src/Tabseeker.Core` — 색인(`FileIndex`), MFT 읽기(`MftReader`), 검색 문법(`SearchQuery`)
- `src/Tabseeker` — WPF 앱: 탭(`MainWindow`), 폴더 탭(`FolderView`), 검색 탭(`SearchView`), 색인 서비스(`IndexService`)
- `tests/Tabseeker.Core.Tests` — xUnit 테스트
- `tests/ui/smoke.ps1` — UI Automation 스모크 테스트
- `tools/make-icon.ps1` — 아이콘 생성

## 변경 기록

- Tabseeker 1.0.0 (2026-11-20): TabSeek 1.0.0에서 이름을 Tabseeker로 바꿔 새로 시작 (실행 파일, 프로젝트, 네임스페이스, 설정 폴더)
- TabSeek 1.0.0 (2026-11-19): 첫 버전. 탭 탐색기, MFT 색인, 즉시 검색, 실시간 반영

