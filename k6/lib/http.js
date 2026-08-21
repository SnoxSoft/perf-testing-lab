import http from 'k6/http';
import exec from 'k6/execution';
import { TARGET } from './config.js';
import { record } from './metrics.js';

// The request helper every scenario goes through, mirroring SendAsync in the
// NBomber suite so that the two suites make the same measurement decisions.

/**
 * Issues one request and records it against the current scenario.
 *
 * @param {string} method
 * @param {string} path
 * @param {{step?: string, okStatuses?: number[], body?: any, headers?: object}} options
 * @returns {{ok: boolean, status: number, json: function, body: string}}
 */
export function request(method, path, options = {}) {
  const { step = null, okStatuses = [200], body = null, headers = {} } = options;

  const response = http.request(method, `${TARGET}${path}`, body, {
    headers,

    // Naming the request keeps k6's own metrics from exploding into one time
    // series per distinct URL once the endurance profile starts generating unique
    // paths. Without this, a run with 20,000 unique report keys produces 20,000
    // URL tags.
    tags: { name: `${method} ${path.split('?')[0].replace(/\/[^/]*$/, (m) => (/\d/.test(m) ? '/:id' : m))}` },

    // Deliberately longer than the slowest pathology. A client timeout shorter
    // than the server's worst case turns a latency measurement into a
    // client-side error and hides the behaviour under test.
    timeout: '60s',

    // No redirects. A redirect silently doubles the request count and the
    // measured latency covers two round trips.
    redirects: 0,
  });

  // k6 reads the body eagerly, so there is no equivalent here of the .NET trap
  // where abandoning the response records time-to-headers as latency. Worth
  // stating rather than assuming: it is a real difference between the two
  // suites, and it is one fewer thing to get wrong in this one.
  const status = response.status;

  // status 0 means the request never completed: a timeout, a refused connection
  // or a DNS failure. Distinguishing those from a server error matters, because
  // one is the target failing and the other is very often the harness.
  const statusCode = status === 0 ? classifyFailure(response) : status;
  const ok = okStatuses.includes(status);

  record(exec.scenario.name, step, response.timings.duration, !ok, statusCode);

  return {
    ok,
    status,
    body: response.body,
    json: (selector) => (selector ? response.json(selector) : response.json()),
  };
}

function classifyFailure(response) {
  const message = String(response.error || '').toLowerCase();
  return message.includes('timeout') || message.includes('deadline') ? 'timeout' : 'transport';
}

export function get(path, options = {}) {
  return request('GET', path, options);
}

export function post(path, options = {}) {
  return request('POST', path, options);
}
