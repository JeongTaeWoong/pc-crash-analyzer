# gold-20261125 — 실제 PC에서 재현한 골드 10건

공개 글(실버)에 나온 PC 문제를 **실제 PC**(Windows 10 Home 22H2, RAM 8GB, VM 없음)에서 재현해 원인과 해결책을 확인한 사례다.
모두 프로그램 밖의 설정이 프로그램을 방해하는 **간섭** 유형이고, 대부분 오류 창 없이 실패한다.

- **평가 전용**이다. 이 사례로 엔진 규칙이나 지시문을 맞추지 않는다.
- 모두 **메커니즘 재현**이다. 원래 앱(AoE4, Minecraft, Epic 런처 등) 대신 같은 런타임(java·python·node·cscript)이나 더미 앱(SaveDummy, PcaProbe)으로 원인을 넣고 뺐다.
- 블랙박스 덤프는 없다. 재현 때 마킹하지 않았고, 이 유형은 지금 수집 항목에 원인 정보가 남지 않는다.

## 판정 기준
각 단계를 반복해 모두 기대대로 나오면 골드로 본다.
- **C(대조):** 원인 없이 정상.
- **I(주입):** 원인을 넣으면 증상.
- **F(해결):** 문서의 해결책을 적용하면 정상.

PCS-032는 단계마다 6회, PCL은 3회 반복했다. PCL-007은 원인이 Windows 기본값이라 C 대신 "고친 뒤 되돌리면 재발(I2)"로 확인했다.

## 목록
| id | 원인 | 증상 | 재현 |
|---|---|---|---|
| PCS-032 | Defender 제어된 폴더 액세스가 앱의 문서 폴더 쓰기를 차단 | 저장 실패(`FileNotFoundException`) | C 6/6 · I 6/6 · F 6/6 |
| PCL-001 | `_JAVA_OPTIONS` 힙 옵션 잔존 | Java 앱 실행 실패 | C·I·F 3/3 |
| PCL-002 | `PYTHONHOME` 잔존 | Python 시작 실패 | C·I·F 3/3 |
| PCL-003 | `NODE_OPTIONS --require` 잔존(VS Code 디버거) | node·npm 실패 | C·I·F 3/3 |
| PCL-004 | PATH에서 System32 누락 | `spawn cmd ENOENT` | C·I·F 3/3 |
| PCL-005 | 남은 수동 프록시(WinINET) | 앱 접속 실패 | C·I·F 3/3 |
| PCL-006 | Windows Script Host 비활성화 | 스크립트 실행 실패 | C·I·F 3/3 |
| PCL-007 | `LongPathsEnabled=0`(기본값) | 긴 경로 파일 실패 | I · F · I2 3/3 |
| PCL-008 | hosts 리디렉션 | 특정 서버 접속 실패 | C·I·F 3/3 |
| PCL-009 | IFEO `Debugger` 잔존 | 특정 프로그램 실행 불가 | C·I·F 3/3 |

## 형식
`gold_dataset.jsonl`은 한 줄에 사례 하나다. 각 줄은 export-case의 `ground_truth` 필드(`symptom`, `cause_category`, `culprit`, `confidence`, `what_user_did`, `what_fixed_it`, `source`)에 `repro`(재현 날짜, C/I/F 결과, 증상 문구)를 더한 것이다. `collector_kind`·`significance`는 덤프가 없어 비어 있다.

`repro.results`와 `source`에 적힌 `collector/experiments/repro-runs/…` 경로는 재현한 PC에만 있다. gitignore 대상이라 저장소에는 없다.
