// k6 load test against a booted Wayfinder.ReferenceApp.
//   k6 run -e BASE_URL=http://127.0.0.1:8080 tests/perf/smoke.js            # smoke (default, about 70s)
//   k6 run -e BASE_URL=... -e PROFILE=stress tests/perf/smoke.js            # ramp to find the limit
// Read paths only. Every page sits behind the reference app's demo login, so each virtual user signs
// in once (k6 keeps a cookie jar per virtual user) and then browses. Point it only at an instance you own.
import http from 'k6/http';
import { check, group, sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://127.0.0.1:8080';
const STRESS = __ENV.PROFILE === 'stress';
const DEMO_PASSWORD = 'wayfinder-demo';

const ramp = (peak) => [
  { duration: '20s', target: peak }, { duration: '40s', target: peak }, { duration: '10s', target: 0 },
];

export const options = {
  scenarios: {
    applicants: { executor: 'ramping-vus', exec: 'applicant', startVUs: 0, stages: ramp(STRESS ? 100 : 20) },
    caseworkers: { executor: 'constant-vus', exec: 'caseworker', vus: STRESS ? 25 : 5, duration: '70s' },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{scenario:applicants}': ['p(95)<800'],
    'http_req_duration{scenario:caseworkers}': ['p(95)<1500'],
    checks: ['rate>0.99'],
  },
};

const ok = (res, name) => check(res, { [`${name} is 200`]: (r) => r.status === 200 });

let signedIn = false;
function signIn(email) {
  if (signedIn) return;
  ok(http.post(`${BASE}/account/login`, { email, password: DEMO_PASSWORD }, { tags: { name: 'login' } }), 'login');
  signedIn = true;
}

export function applicant() {
  signIn('applicant@example.test');
  group('applicant browse', () => {
    for (const path of ['/', '/apply', '/premium']) {
      ok(http.get(`${BASE}${path}`, { tags: { name: path } }), path);
      sleep(1);
    }
  });
}

export function caseworker() {
  signIn('caseworker@example.test');
  group('caseworker worklist', () => {
    ok(http.get(`${BASE}/caseworker/queue`, { tags: { name: '/caseworker/queue' } }), '/caseworker/queue');
    sleep(1);
  });
}
