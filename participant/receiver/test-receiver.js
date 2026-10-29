// Code.gs 를 Node 에서 Apps Script 서비스 흉내로 돌려, C# ReceiverRules 와 같은 판정을 내는지 확인한다.
// 사용: node participant/receiver/test-receiver.js <실제 봉투 폴더(StudyTools receive 의 --out)> <토큰>
// Google 에 배포하지 않고 수신 규칙만 시험한다 — 실제 Drive·메일·302 리디렉션 동작은 배포 후 --selftest 로 확인.
'use strict';
const fs = require('fs');
const path = require('path');
const vm = require('vm');

const [, , inboxDir, token] = process.argv;
if (!inboxDir || !token) { console.error('usage: node test-receiver.js <inbox dir> <token>'); process.exit(64); }

const created = [];
let storageUsed = 1e9, storageLimit = 15e9;
const cacheStore = {};
const folders = {};
const sandbox = {
  PropertiesService: { getScriptProperties: () => ({ getProperty: k => ({ TOKEN: token, FOLDER_ID: 'root', STUDY: 'pca-2026' })[k] || null }) },
  CacheService: { getScriptCache: () => ({ get: k => cacheStore[k] || null, put: (k, v) => { cacheStore[k] = v; } }) },
  LockService: { getScriptLock: () => ({ tryLock: () => true, releaseLock: () => {} }) },
  DriveApp: {
    getStorageUsed: () => storageUsed,
    getStorageLimit: () => storageLimit,
    getFolderById: () => ({
      getFoldersByName: name => { const f = folders[name]; return { hasNext: () => !!f, next: () => f }; },
      createFolder: name => (folders[name] = { name, createFile: (fn, body, mime) => created.push({ folder: name, fn, len: body.length, mime }) }),
    }),
  },
  Utilities: { getUuid: () => '12345678-aaaa-bbbb-cccc-1234567890ab' },
  ContentService: { createTextOutput: s => ({ setMimeType: () => ({ text: s }) }), MimeType: { JSON: 'json' } },
  Session: { getEffectiveUser: () => ({ getEmail: () => 'team@example.com' }) },
  MailApp: { sendEmail: (to, subj) => created.push({ mail: to, subj }) },
  Logger: { log: () => {} },
  console: { error: () => {}, log: () => {} },
};
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(path.join(__dirname, 'Code.gs'), 'utf8'), sandbox);

const post = body => JSON.parse(sandbox.doPost({ postData: { contents: body } }).text);
let fails = 0;
const expect = (label, got, want) => {
  const ok = got === want;
  if (!ok) fails++;
  console.log(`${ok ? 'OK  ' : 'FAIL'} ${label}: ${got}${ok ? '' : ' (기대 ' + want + ')'}`);
};

// 1) 실제 C# 봉투는 전부 받아야 한다
const real = [];
for (const d of fs.readdirSync(inboxDir)) {
  const p = path.join(inboxDir, d);
  if (!fs.statSync(p).isDirectory()) continue;
  for (const f of fs.readdirSync(p)) if (f.endsWith('.pcae.json')) real.push(fs.readFileSync(path.join(p, f), 'utf8'));
}
if (real.length === 0) { console.error('봉투 없음'); process.exit(1); }
for (const body of real) {
  const env = JSON.parse(body);
  expect(`실제 봉투 ${env.kind}/${env.name}`, post(body).ok, true);
}
const sample = JSON.parse(real[0]);

// 2) 거부해야 하는 것 — ReceiverRulesTests 와 같은 경우
const mutate = f => { const e = JSON.parse(JSON.stringify(sample)); f(e); return JSON.stringify(e); };
expect('빈 본문', post('').error, 'empty');
expect('JSON 아님', post('not json').error, 'bad_json');
expect('버전 2', post('{"v":2}').error, 'bad_version');
expect('토큰 틀림', post(mutate(e => { e.token = 'tok_other_0123456789'; })).error, 'bad_token');
expect('대문자 참여 ID', post(mutate(e => { e.pid = 'ABCD'; })).error, 'bad_request');
expect('경로 이동 이름', post(mutate(e => { e.name = '../../etc/passwd'; })).error, 'bad_request');
expect('이름 ".."', post(mutate(e => { e.name = '..'; })).error, 'bad_request');           // 10/29 검토 지적
expect('장치 이름 CON', post(mutate(e => { e.name = 'CON'; })).error, 'bad_request');
expect('배열 참여 ID', post(mutate(e => { e.pid = [e.pid]; })).error, 'bad_request');       // 문자열 강제 변환 방지
expect('서명 없음', post(mutate(e => { delete e.sig; })).error, 'bad_request');
expect('서명 방식 다름', post(mutate(e => { e.sigAlg = 'none'; })).error, 'bad_request');
expect('오류 응답에 상세 없음', 'detail' in post(mutate(e => { e.kind = 'exfil'; })), false);
expect('알 수 없는 kind', post(mutate(e => { e.kind = 'exfil'; })).error, 'bad_request');
expect('다른 연구 ID', post(mutate(e => { e.study = 'other-study'; })).error, 'bad_request');
expect('ct 가 base64 아님', post(mutate(e => { e.ct = '<script>'; })).error, 'bad_request');
expect('너무 큼', post('a'.repeat(10 * 1024 * 1024 + 1)).error, 'too_large');

// 3) 참여자별 상한
for (const k of Object.keys(cacheStore)) delete cacheStore[k];
let last;
for (let i = 0; i < 151; i++) last = post(real[0]);
expect('151번째 같은 참여자 → 상한', last.error, 'rate_limited');

// 3b) 바이트 상한(참여자 50MB/6시간)
for (const k of Object.keys(cacheStore)) delete cacheStore[k];
const bigBody = mutate(e => { e.ct = 'A'.repeat(9 * 1024 * 1024); });
let bigLast;
for (let i = 0; i < 6; i++) bigLast = post(bigBody);
expect('9MB × 6 → 바이트 상한', bigLast.error, 'rate_limited');

// 3c) 저장 공간 80% 초과 → 받지 않음
for (const k of Object.keys(cacheStore)) delete cacheStore[k];
storageUsed = 12.5e9;
expect('저장 공간 80% 초과', post(real[0]).error, 'rate_limited');
storageUsed = 1e9;

// 4) 철회는 메일 알림
for (const k of Object.keys(cacheStore)) delete cacheStore[k];
post(mutate(e => { e.kind = 'withdraw'; e.name = 'withdraw-20261029-120000'; }));
expect('철회 알림 메일', created.some(c => c.mail && c.subj.includes(sample.pid)), true);

console.log(`\n저장 ${created.filter(c => c.fn).length}건, 폴더 ${Object.keys(folders).join(', ')} — 실패 ${fails}`);
process.exit(fails === 0 ? 0 : 1);
