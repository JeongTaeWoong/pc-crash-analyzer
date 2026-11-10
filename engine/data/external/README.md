# engine/data/external — 외부 공개 사례 (실측 아님)

포럼·이슈 트래커·벤더 문서에 공개된 로그를 엔진 스냅샷 형식(`engine/docs/snapshot_schema.md` v1.2)으로 옮긴 참고 사례다.
**팀이 측정한 데이터가 아니다.** 모든 파일에 `provenance.type = "external-reconstructed"` 와 출처 URL이 붙어 있다.

## 쓰는 법

- 용도: 엔진이 실제 사용자 보고에서 어떻게 틀리는지 찾는 탐색, 규칙의 근거 확인.
- 쓰면 안 되는 곳: `docs/conflict-test-plan.md` 3.4 지표(가설 정확도 등), "수집한 로그" 건수, 발표의 정량 주장.
- `engine/data/samples/`, `cases/`, 참여자 데이터(`study-decrypted/`)와 섞지 않는다.
- 커뮤니티 글(D등급)은 같은 문서 4.1 기준대로 "보고된 사례"로만 인용한다.

## 파일 규칙 (`tools/import-external-report.ps1`)

- 출처에 없는 값은 만들지 않고 필드를 생략한다. 엔진은 없는 필드를 '확인 불가'로 처리한다.
- `marked_at` 은 게시일이다. 사건 시각이 아니므로 시간 상관 근거로 해석하지 않는다. 게시일이 없으면 필드가 없다.
- 이벤트에는 `time` 이 없다. 글에 적힌 시각을 신뢰할 수 없어 넣지 않았다.
- `provenance.perf_evidence` 의 CPU·메모리 수치는 글에 적힌 값이다. `resource_trends`(60분 측정값)로 옮기지 않는다.
- `ground_truth` 는 작성자가 해결을 확인했고 원인이 적혀 있을 때만 붙는다.
- `provenance.confidence`: `official` 벤더 문서 / `user` 작성자 확인 / `guess` 그 외 또는 검토에서 라벨 보류(`label_withheld`).

## 현재 내용 (11/10 1차)

커뮤니티 로그 34건, 그중 `ground_truth` 7건. 7건 중 ext-006·015·020·023 은 원문과 대조했다.

출처 편향: Reddit·ElevenForum·TenForums·Sysnative 는 조사 도구로 열리지 않아 빠졌다. Microsoft Q&A, Tom's Hardware, Steam, DisplayLink 포럼이 대부분이다. 값은 웹페이지 추출 결과이므로 인용 전에 원문을 확인한다.

## 다시 만들기

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -Command "& 'tools\import-external-report.ps1' -In <reports.json> -LabelHold 'ext-007','ext-029','ext-031'"
```
