# ReplayVideo — 리플레이(.yrpX) → 보드 영상(MP4)

EDOPro 리플레이 파일을 읽어 **2D 보드 애니메이션 영상**으로 만드는 단독 CLI 도구입니다.
게임 엔진(ygopro-core) 없이, 리플레이에 들어 있는 **MSG_\* 스트림**만으로 상태를 재구성합니다.

> **빌드 준비 — `_shared` 폴더가 필요합니다**
> 이 저장소는 `..\_shared\ReplaySim.cs` 를 **링크 컴파일**합니다.
> 클론했다면 형제 폴더에 `_shared` 를 두세요: `<작업폴더>\ReplayVideo` + `<작업폴더>\_shared\ReplaySim.cs`
> (배포 백업 ZIP 에는 `_shared/` 가 들어 있어 그대로 풀면 바로 빌드됩니다.)

## 요구 사항

| 항목 | 내용 |
|---|---|
런타임 | .NET 8 (Windows). `dotnet publish` 로 단일 exe 가능 |
**ffmpeg** | MP4 인코딩에 사용 (없으면 PNG 시퀀스만 생성) |
카드 이미지 | **EDOPro `pics\<카드ID>.jpg` → 없으면 YGOPRODeck 이미지 자동 다운로드** |
카드 정보 | EDOPro `*.cdb`(한국어 우선) → 없으면 YGOPRODeck API |

## EDOPro 없이 쓰기 (cdb·pics 가 없을 때)

cdb 파일이 하나도 없어도 동작합니다. 이름·공격력·설명은 **YGOPRODeck API**, 그림은 **이미지 API** 에서 받습니다.

```
카드 정보  GET https://db.ygoprodeck.com/api/v7/cardinfo.php?id=<ID>&language=ko   ← 한국어(공식명) 우선
           실패(404) → &language 없이(영문)
이미지      GET https://images.ygoprodeck.com/images/cards/<ID>.jpg
캐시        %LOCALAPPDATA%\ReplayVideo\cards\<ID>.ko.json / <ID>.en.json
            %LOCALAPPDATA%\ReplayVideo\imgcache\<ID>.jpg
```

- **이름/설명 우선순위**: ①한국어 cdb ②**API 한국어** ③영문 cdb ④API 영문 ⑤`#ID`
  - 실측 `14558127` → `하루 우라라` + 한국어 설명 / `67115133` → `현람한 헌영`
  - API 에 한국어가 없는 카드(신규·OCG 선행)는 영문 이름으로 표시됩니다.
- **그림**: 로컬 `pics` 가 있으면 그것을, 없으면 API 이미지를 받아 캐시합니다.
  `--img-api` 를 주면 로컬보다 API 이미지를 우선(고해상).
- **컷인용 고해상도**: 발동 컷인에 쓰는 카드만 API 이미지를 따로 받아 **선명하게** 그립니다(로컬 pics 가 177×254 로 작기 때문).
- 시작 시 등장 카드를 **4 병렬로 미리 조회** · `--no-api` 로 모두 끄면 cdb/로컬 pics 만 사용(없는 카드는 `#ID`)

## 발동 컷인 (2026-11-05)

마함존 카드가 발동하거나(세트→발동 포함) 뒷면 몬스터가 뒤집히면, 그 카드를 **화면 가운데에 크게** 띄웁니다.

- 배경을 어둡게(dim 160) + 금색 테두리 + 상단 배너 `발동 !  <카드명>`
- **5프레임** 유지(12fps 기준 약 0.4초) — 보드 프레임 3장 + 컷인 정지 2장
- 카드 폭: 가로 320px / 세로 520px · 그림은 API 고해상 우선, 없으면 로컬 pics

## 세로(모바일) 레이아웃 `--layout portrait`

`1080×1920` 세로 화면. 카드가 **190×273** 로 커지고, 손패는 가로 스트립, 묘지/제외는 2×2 블록.

- `--no-translate` 로 번역 끄기
- 효과 텍스트 번역 캐시: `%LOCALAPPDATA%\ReplayVideo\trans\<sha1>.txt`
- 설명이 이미 한국어(한국어 cdb / API ko)면 번역을 건너뜁니다

## 사용법

```powershell
ReplayVideo.exe <리플레이.yrpX> <출력.mp4> [옵션]

옵션
  --pics <폴더>     카드 이미지 폴더 (기본 D:\edopro\pics)
  --cdb <파일>      카드 DB 시작점 (지정 폴더 상위의 *.cdb 를 모두 엽니다 · 한국어 cdb 우선)
  --fps <N>         영상 프레임레이트 (기본 12)
  --frames <폴더>   PNG 시퀀스를 남길 폴더
  --layout portrait 세로(모바일) 레이아웃 (기본 landscape)
  --img-api         로컬 pics 보다 내려받은 API 이미지를 우선
  --no-api          카드 정보/이미지를 인터넷에서 받지 않음
  --no-translate    효과 텍스트 기계 번역 안 함
  --dump            패킷 구조 덤프(개발용)
```

예시:
```powershell
ReplayVideo.exe "D:\edopro\replay\2026-08-24 13-42-47.yrpX" "out.mp4" --pics D:\edopro\pics --fps 12
ReplayVideo.exe "replay.yrpX" "out.mp4" --cdb ".\none.cdb" --layout portrait      # EDOPro 없이
```

## 화면 구성

```
┌ TURN n · 페이즈 ─ 이벤트 캡션 ──────────────────────────────┐
│ 상대 패        [마함존 5]                                   │
│  (썸네일)      [몬스터존 5]        상대 묘지 / 상대 제외     │
│  LP …          체인 패널          내 묘지 / 내 제외          │
│ 내 패          [몬스터존 5]                                 │
│  (썸네일)      [마함존 5]                                   │
│ 패/묘지/제외/덱/엑스트라 장수 · 최근 카드 이름               │
└──────────────────────────────────────────────────────────────┘
```

- 카드: 이미지 + **이름 바** + **ATK 배지**, 뒷면(SET)은 카드백
- **발동 컷인**: 발동한 카드를 화면 가운데에 크게 + 배너 `발동 !  <카드명>`
- **세트 카드가 발동되면 앞면으로 뒤집어 이미지 표시** + 금색 `발동` 테두리 (같은 이유로 반전 소환도 공개)
- **효과 텍스트**: 마지막 이벤트 카드의 설명을 필드 아래 박스에 표시(cdb `desc` 또는 API `desc`)
- **발동 위치**: 체인 캡션·체인 패널에 `[패] [필드] [마함] [묘지] [제외]` 표기
- **세로 레이아웃**(`--layout portrait`): 1080×1920 — 카드 190×273, 필드 위아래, 손패 가로 스트립, 묘지/제외 2×2
- **효과 텍스트 한국어 번역**(기본 켜짐): `translate.googleapis.com` 로 번역 후 캐시. `--no-translate` 로 끔
- 이동/소환: 직전 스냅샷→현재 스냅샷 **3프레임 보간**
- 공격: 공격자 **빨강**, 대상 **주황** / 데미지: LP 옆 큰 숫자
- 체인: 카드에 **C1~C6** 뱃지(보라=발동, 청록=해결, 빨강+X=무효) + 가운데 **체인 패널**
- 덱 변화: `MOVE`/`DRAW` 캡션에 **`(덱 N)`** 표시

## 알려진 한계

- **선택/응답은 리플레이에 저장되지 않는다**(`MSG_SELECT_*` 0건) → "AI 가 무엇을 골랐는지"는 표시 불가
- EDOPro 원본의 **3D 연출·이펙트·사운드**는 리플레이에 없음 → 2D 보드만
- 실제 플레이 **타이밍**도 없음 → 이벤트당 3프레임 고정
- 커스텀 카드 중 외부 DB/CDB 에 없는 ID 는 `#ID` 로 표시
- **로컬 pics 파일명이 실제 카드와 다른 경우** 그 그림이 그대로 쓰입니다
  (예: `pics\67115133.jpg` 가 다른 카드 그림 → `--img-api` 로 API 이미지 사용)
- API 에 한국어가 없는 카드는 **영문 이름**으로 나옵니다(설명은 기계 번역)
- API 이미지는 OCG 선행 카드면 **일본판** 일러스트일 수 있습니다

## 저장 위치 (기본 = D:)

C: 용량이 부족하므로 **출력·캐시·프레임은 D: 를 기본**으로 씁니다(`D:` → `E:` → `F:` → `%LOCALAPPDATA%` 순).

| 용도 | 경로 |
|---|---|
| 캐시(카드 정보·이미지·번역) | `D:\ReplayVideo\cache\{cards,imgcache,trans}` |
| 임시 프레임 PNG | `D:\ReplayVideo\frames\rv-<guid>` |
| GUI 기본 출력 | `D:\ReplayVideo\out\<리플레이이름>.mp4` |

예전 `%LOCALAPPDATA%\ReplayVideo` 캐시는 첫 실행 때 **파일 단위로 자동 이전**됩니다.

## 구성 (엔진 공용)

- `ReplayVideo\Program.cs` — CLI + **윈도우 GDI 렌더러**(레이아웃/ffmpeg 인코딩)
- `..\_shared\ReplaySim.cs` — **상태 머신 정본**(`ReplayVideoCore.Sim`) · 안드로이드 앱도 같은 파일을 링크 컴파일
- `ReplayVideoApp\` — 윈도우 GUI(이 프로젝트를 참조) · `ReplayVideoAndroid\` — 안드로이드 앱(Skia + MediaCodec)

## 관련 문서

- `D:\rea-out\windbot-ai-editor\61-replay-video-prototype.md` — 설계·실측(MSG 구조, START/DRAW 레이아웃, 성능)
- `D:\rea-out\edopro-41.0.2\replay-format.md` — 리플레이 파일 구조 (REA 분석)
