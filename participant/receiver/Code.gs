/**
 * PC 블랙박스 수신기 — Google Apps Script 웹 앱 (팀 Google 계정에 배포)
 *
 * 하는 일: 참여자 PC 가 보낸 "암호 봉투"(JSON)를 형식·크기·토큰만 확인하고 Drive 폴더에 참여 ID 별로 저장한다.
 * 내용은 참여자 PC 에서 팀 공개키로 암호화되어 있어 이 스크립트(와 Google)는 열 수 없다. 복호화·서명 확인은 팀 PC 의 StudyTools 로만.
 *
 * 규칙은 participant/StudyCore/ReceiverRules.cs 와 같다 — 한쪽을 바꾸면 다른 쪽도 바꾸고 test-receiver.js 를 돌린다.
 * 응답: {"ok":true,"stored":파일이름} / {"ok":false,"error":코드}
 *   코드: empty, too_large, bad_json, bad_version, bad_token, bad_request, rate_limited, server
 *   (Apps Script 는 HTTP 상태 코드를 바꿀 수 없어 항상 200 — 클라이언트는 본문의 ok/error 로 판단한다)
 *   오류 내용(예외 메시지)은 응답에 싣지 않고 실행 기록(console.error)에만 남긴다.
 *
 * 설정(한 번): 편집기에서 setupFolder 실행 → 프로젝트 설정 → 스크립트 속성에 TOKEN 추가
 *   FOLDER_ID  저장 폴더 ID (setupFolder 가 만든다)
 *   STUDY      연구 ID (기본 pca-2026)
 *   TOKEN      participant/study.local.json 의 token 값
 * 배포: 배포 → 새 배포 → 유형 "웹 앱", 실행 사용자 "나", 액세스 권한 "모든 사용자" → /exec 주소를 study.local.json 의 endpoint 에.
 */

var MAX_CHARS = 10 * 1024 * 1024;                 // ReceiverRules.MaxBodyChars
var PID_RX = /^[a-z0-9]{8,32}$/;
var NAME_RX = /^([0-9]{8}-[0-9]{6}-[a-z0-9-]{1,40}|hb-[0-9]{8}-[0-9]{6}|withdraw-[0-9]{8}-[0-9]{6})$/;  // 점 없음 — '..' 불가
var STUDY_RX = /^[a-z0-9-]{3,32}$/;
var B64_RX = /^[A-Za-z0-9+\/=]+$/;
var KINDS = ['dump', 'annotation', 'heartbeat', 'withdraw'];
var SIG_ALG = 'ES256';
// 6시간 칸(고정 시각 구간)마다 상한 — 주소·토큰이 새어도 Drive(15GB, Gmail 과 공유)가 차지 않게
var BUCKET_SECONDS = 21600;
var PER_PID_COUNT = 150, PER_PID_BYTES = 50 * 1024 * 1024;
var GLOBAL_COUNT = 2000, GLOBAL_BYTES = 300 * 1024 * 1024;
var STORAGE_MAX_FRACTION = 0.8;                   // 계정 저장 공간 80% 를 넘으면 받지 않는다(재시도 대상)

function doPost(e) {
  try {
    var props = PropertiesService.getScriptProperties();
    var token = props.getProperty('TOKEN');
    var folderId = props.getProperty('FOLDER_ID');
    var study = props.getProperty('STUDY') || 'pca-2026';
    if (!token || !folderId) { console.error('not configured'); return reply_({ ok: false, error: 'server' }); }

    var body = e && e.postData ? e.postData.contents : '';
    if (!body) return reply_({ ok: false, error: 'empty' });
    if (body.length > MAX_CHARS) return reply_({ ok: false, error: 'too_large' });

    var env;
    try { env = JSON.parse(body); } catch (x) { return reply_({ ok: false, error: 'bad_json' }); }
    if (!env || typeof env !== 'object' || Array.isArray(env) || env.v !== 1) return reply_({ ok: false, error: 'bad_version' });
    if (typeof env.token !== 'string' || env.token !== token) return reply_({ ok: false, error: 'bad_token' });
    if (!isStr_(env.study) || !STUDY_RX.test(env.study) || env.study !== study) return reply_({ ok: false, error: 'bad_request' });
    if (!isStr_(env.pid) || !PID_RX.test(env.pid) || !isStr_(env.name) || !NAME_RX.test(env.name) ||
        !isStr_(env.kind) || KINDS.indexOf(env.kind) < 0) {
      return reply_({ ok: false, error: 'bad_request' });
    }
    if (env.sigAlg !== SIG_ALG) return reply_({ ok: false, error: 'bad_request' });
    var fields = ['ek', 'iv', 'tag', 'ct', 'spk', 'sig'];
    for (var i = 0; i < fields.length; i++) {
      var f = env[fields[i]];
      if (!isStr_(f) || !B64_RX.test(f)) return reply_({ ok: false, error: 'bad_request' });
    }

    var lock = LockService.getScriptLock();
    if (!lock.tryLock(20000)) return reply_({ ok: false, error: 'server' });
    var fileName;
    try {
      var cache = CacheService.getScriptCache();
      var bucket = Math.floor(Date.now() / 1000 / BUCKET_SECONDS);   // 고정 구간 — 매 요청마다 만료가 늘어나지 않는다
      var k = function (name) { return 'b' + bucket + '_' + name; };
      var n = function (name) { return Number(cache.get(k(name)) || 0); };
      var len = body.length;
      if (n('pn_' + env.pid) >= PER_PID_COUNT || n('pb_' + env.pid) + len > PER_PID_BYTES ||
          n('gn') >= GLOBAL_COUNT || n('gb') + len > GLOBAL_BYTES) {
        return reply_({ ok: false, error: 'rate_limited' });
      }
      var used = DriveApp.getStorageUsed(), limit = DriveApp.getStorageLimit();
      if (limit > 0 && used + len > limit * STORAGE_MAX_FRACTION) {
        console.error('storage nearly full: ' + used + '/' + limit);
        return reply_({ ok: false, error: 'rate_limited' });
      }
      cache.put(k('pn_' + env.pid), String(n('pn_' + env.pid) + 1), BUCKET_SECONDS);
      cache.put(k('pb_' + env.pid), String(n('pb_' + env.pid) + len), BUCKET_SECONDS);
      cache.put(k('gn'), String(n('gn') + 1), BUCKET_SECONDS);
      cache.put(k('gb'), String(n('gb') + len), BUCKET_SECONDS);

      var root = DriveApp.getFolderById(folderId);
      var it = root.getFoldersByName(env.pid);
      var folder = it.hasNext() ? it.next() : root.createFolder(env.pid);
      var rand = Utilities.getUuid().replace(/-/g, '').slice(0, 8);
      fileName = env.kind + '_' + env.name + '_' + rand + '.pcae.json';   // ReceiverRules.StoredFileName
      folder.createFile(fileName, body, 'application/json');
    } finally {
      lock.releaseLock();
    }
    if (env.kind === 'withdraw') notifyWithdraw_(env.pid);
    return reply_({ ok: true, stored: fileName });
  } catch (err) {
    console.error(err);
    return reply_({ ok: false, error: 'server' });
  }
}

/** 상태 확인용: 브라우저로 /exec 를 열면 {"ok":true,...} — 내용은 아무것도 보여 주지 않는다. */
function doGet() {
  return reply_({ ok: true, service: 'pca-receiver', v: 1 });
}

/** 편집기에서 한 번 실행: 내 Drive 에 저장 폴더를 만들고 FOLDER_ID·STUDY 를 스크립트 속성에 넣는다. TOKEN 은 직접 넣는다. */
function setupFolder() {
  var props = PropertiesService.getScriptProperties();
  if (props.getProperty('FOLDER_ID')) {
    Logger.log('이미 설정됨: FOLDER_ID=' + props.getProperty('FOLDER_ID'));
    return;
  }
  var folder = DriveApp.createFolder('PC블랙박스-수신 (암호화됨, 공유 금지)');
  props.setProperty('FOLDER_ID', folder.getId());
  if (!props.getProperty('STUDY')) props.setProperty('STUDY', 'pca-2026');
  Logger.log('만든 폴더: ' + folder.getUrl() + ' — 이제 스크립트 속성에 TOKEN 을 넣고 웹 앱으로 배포하세요.');
}

/**
 * 참여 철회 요청이 오면 배포한 계정으로 메일을 보낸다(내용 없이 참여 ID 만).
 * 서명은 여기서 확인할 수 없다 — 실제 삭제 전에 StudyTools decrypt 가 "서명 확인됨"으로 표시한 철회인지 볼 것.
 */
function notifyWithdraw_(pid) {
  try {
    var me = Session.getEffectiveUser().getEmail();
    if (me) MailApp.sendEmail(me, '[PC 블랙박스] 참여 철회 요청: ' + pid,
      '참여 ID ' + pid + ' 의 참여 철회 요청이 도착했습니다.\n' +
      'StudyTools decrypt 로 서명이 확인된 철회인지 본 뒤, Drive 수신 폴더의 ' + pid + ' 폴더와 팀 PC 의 복호화본을 지워 주세요.');
  } catch (x) { /* 메일 권한이 없으면 건너뛴다 — StudyTools decrypt 가 철회를 따로 알린다 */ }
}

function isStr_(v) { return typeof v === 'string'; }

function reply_(o) {
  return ContentService.createTextOutput(JSON.stringify(o)).setMimeType(ContentService.MimeType.JSON);
}
