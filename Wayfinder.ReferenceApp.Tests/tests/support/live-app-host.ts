/**
 * Boots the real Wayfinder.AppHost stack (Wayfinder.ReferenceApp + SafetyNetUnderwriting) for
 * specs that need genuine cross-process behaviour — Aspire service discovery, a real webhook
 * call between two separately-running apps — that Wayfinder.ReferenceApp.Tests' own default
 * `playwright.config.ts` can't exercise (it boots Wayfinder.ReferenceApp directly via
 * `dotnet run`, with no Aspire orchestration, so `SafetyNetUnderwritingClient`'s
 * `http://safetynet-underwriting` address never resolves — see docs/guides/support-systems.md).
 *
 * Precedent: Umbraco.Prism's `UmbracoPrism.Client/tests/support/live-app-host.ts` does the same
 * thing for its own AppHost — this is a deliberately leaner version, proportionate to
 * Wayfinder's actual stack (two plain in-memory ASP.NET Core apps, no Docker, no auth realm to
 * seed), not a port of Umbraco.Prism's full machinery (Docker/Keycloak checks, unseeded-splash-
 * page detection, stall-recovery restart) — none of which has anything to be proportionate to
 * here. The core discipline carries over: don't call the stack "ready" until every resource it's
 * actually made of answers, or specs against it are fragile by construction.
 */
import { spawn, spawnSync, type ChildProcessWithoutNullStreams } from 'node:child_process';
import http from 'node:http';
import https from 'node:https';
import path from 'node:path';

// process.cwd(), not import.meta.dirname — this package.json has no "type": "module", so a
// genuine ESM-only feature like import.meta.dirname isn't available under Playwright's own TS
// transform. Playwright always runs with cwd set to the project directory containing the config
// in use (Wayfinder.ReferenceApp.Tests/), one level below the repo root.
const repoRoot = path.resolve(process.cwd(), '..');
const appHostProject = path.join(repoRoot, 'Wayfinder.AppHost');
const readinessTimeoutMs = 180_000; // Aspire + two cold-starting dotnet apps; generous for a slow first run.
const readinessPollIntervalMs = 2_000;
const probeTimeoutMs = 5_000;

const readinessChecks = [
  // Public, unauthenticated route — proves ReferenceApp itself is up without needing a login flow.
  { name: 'Wayfinder.ReferenceApp', url: 'https://localhost:7286/account/login', allowedStatuses: [200] },
  { name: 'SafetyNetUnderwriting', url: 'https://localhost:7301/queue', allowedStatuses: [200] }
] as const;

const requiredPorts = [
  { name: 'Wayfinder.ReferenceApp (https)', port: 7286 },
  { name: 'Wayfinder.ReferenceApp (http)', port: 5286 },
  { name: 'SafetyNetUnderwriting (https)', port: 7301 },
  { name: 'SafetyNetUnderwriting (http)', port: 5301 }
] as const;

type ProbeResult = { status: number | null; error: string | null };
type ReadinessStatus = { name: string; ok: boolean; observed: string };

export class LiveAppHost {
  private child: ChildProcessWithoutNullStreams | undefined;
  private readonly logs: string[] = [];

  async start(): Promise<void> {
    if (this.child) {
      return;
    }

    await this.ensurePortsAreAvailable();

    this.child = spawn('dotnet', ['run', '--project', appHostProject, '--no-launch-profile'], {
      cwd: repoRoot,
      env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: process.env.DOTNET_CLI_TELEMETRY_OPTOUT ?? '1' },
      stdio: ['ignore', 'pipe', 'pipe']
    });

    this.child.stdout.on('data', chunk => this.captureLog('stdout', chunk));
    this.child.stderr.on('data', chunk => this.captureLog('stderr', chunk));

    try {
      await this.waitForReadiness();
    } catch (error) {
      await this.stop().catch(() => undefined);
      throw error;
    }
  }

  isRunning(): boolean {
    return this.child !== undefined && this.child.exitCode === null;
  }

  async stop(): Promise<void> {
    const child = this.child;
    this.child = undefined;

    if (child) {
      const exited = waitForExit(child);
      sendSignal(child.pid, 'SIGINT');
      const graceful = await Promise.race([exited.then(() => true), delay(20_000).then(() => false)]);

      if (!graceful) {
        sendSignal(child.pid, 'SIGTERM');
        await Promise.race([exited, delay(10_000)]);
      }
    }

    // Aspire's DCP orchestrator manages the actual child app processes separately from the
    // `dotnet run --project Wayfinder.AppHost` process itself — SIGINT to the parent doesn't
    // reliably cascade to them. Port-based cleanup is the actual source of truth for "did this
    // really stop", the same discipline Umbraco.Prism's own live-app-host.ts uses.
    if (await waitForPortsToStop(15_000)) {
      return;
    }

    await terminatePortListeners('SIGTERM');
    if (await waitForPortsToStop(15_000)) {
      return;
    }

    await terminatePortListeners('SIGKILL');
    if (await waitForPortsToStop(15_000)) {
      return;
    }

    throw new Error(`Timed out waiting for the Aspire-hosted stack's ports to stop.\n\nRecent logs:\n${this.formatLogs()}`);
  }

  private captureLog(stream: 'stdout' | 'stderr', chunk: Buffer): void {
    const lines = chunk
      .toString()
      .split(/\r?\n/)
      .map(line => line.trim())
      .filter(Boolean)
      .map(line => `[${stream}] ${line}`);

    this.logs.push(...lines);
    if (this.logs.length > 300) {
      this.logs.splice(0, this.logs.length - 300);
    }
  }

  private formatLogs(): string {
    return this.logs.length > 0 ? this.logs.join('\n') : '(no AppHost logs captured)';
  }

  private async ensurePortsAreAvailable(): Promise<void> {
    const occupied = getOccupiedPorts();
    if (occupied.length === 0) {
      return;
    }

    throw new Error(
      `The live-stack Playwright suite owns the AppHost lifecycle and needs these ports free: ` +
        occupied.map(port => `${port.name} (${port.port}) [pid ${port.pids.join(', ')}]`).join(', ') +
        `. Stop whatever's already running (a leftover 'dotnet run --project Wayfinder.AppHost'?) and retry.`
    );
  }

  private async waitForReadiness(): Promise<void> {
    const start = Date.now();
    let latest: ReadinessStatus[] = [];

    while (Date.now() - start < readinessTimeoutMs) {
      if (this.child && this.child.exitCode !== null) {
        throw new Error(`AppHost exited before the stack became ready.\n\nRecent logs:\n${this.formatLogs()}`);
      }

      latest = await this.getReadinessStatuses();
      if (latest.every(status => status.ok)) {
        return;
      }

      await delay(readinessPollIntervalMs);
    }

    const diagnostics = latest
      .map(status => `  - ${status.name}: ${status.ok ? 'ready' : `not ready (${status.observed})`}`)
      .join('\n');
    throw new Error(
      `Timed out waiting ${Math.round(readinessTimeoutMs / 1000)}s for the AppHost stack to become ready.\n\n` +
        `Readiness:\n${diagnostics}\n\nPorts:\n${formatPortDiagnostics()}\n\nRecent logs:\n${this.formatLogs()}`
    );
  }

  private async getReadinessStatuses(): Promise<ReadinessStatus[]> {
    return Promise.all(
      readinessChecks.map(async check => {
        const result = await probe(check.url);
        const allowedStatuses = check.allowedStatuses as readonly number[];
        const ok = result.status !== null && allowedStatuses.includes(result.status);
        const observed = result.status !== null ? `HTTP ${result.status}` : `no response (${result.error})`;
        return { name: check.name, ok, observed };
      })
    );
  }
}

async function probe(urlString: string): Promise<ProbeResult> {
  const url = new URL(urlString);
  const client = url.protocol === 'https:' ? https : http;

  return new Promise(resolve => {
    let settled = false;
    const settle = (result: ProbeResult) => {
      if (!settled) {
        settled = true;
        resolve(result);
      }
    };

    const request = client.request(url, { method: 'GET', rejectUnauthorized: false }, response => {
      response.resume(); // drain, we only care about the status code
      response.on('end', () => settle({ status: response.statusCode ?? null, error: null }));
    });

    request.setTimeout(probeTimeoutMs, () => {
      request.destroy();
      settle({ status: null, error: `timed out after ${probeTimeoutMs}ms` });
    });
    request.on('error', error => settle({ status: null, error: error instanceof Error ? error.message : String(error) }));
    request.end();
  });
}

function formatPortDiagnostics(): string {
  return requiredPorts.map(({ name, port }) => `  - ${name} (${port}): ${describePortListener(port)}`).join('\n');
}

function describePortListener(port: number): string {
  const pids = findListeningPids(port);
  return pids.length > 0 ? `listening [pid ${pids.join(', ')}]` : 'not listening';
}

function findListeningPids(port: number): number[] {
  const result = spawnSync('lsof', ['-t', `-iTCP:${port}`, '-sTCP:LISTEN'], { encoding: 'utf8' });
  if (result.status !== 0 || !result.stdout.trim()) {
    return [];
  }

  return result.stdout
    .trim()
    .split(/\s+/)
    .map(Number)
    .filter(pid => Number.isInteger(pid));
}

function getOccupiedPorts(): Array<{ name: string; port: number; pids: number[] }> {
  return requiredPorts.map(({ name, port }) => ({ name, port, pids: findListeningPids(port) })).filter(p => p.pids.length > 0);
}

async function terminatePortListeners(signal: 'SIGTERM' | 'SIGKILL'): Promise<void> {
  const pids = new Set<number>();
  for (const { port } of requiredPorts) {
    for (const pid of findListeningPids(port)) {
      pids.add(pid);
    }
  }
  for (const pid of pids) {
    sendSignal(pid, signal);
  }
}

async function waitForPortsToStop(timeoutMs: number): Promise<boolean> {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    if (getOccupiedPorts().length === 0) {
      return true;
    }
    await delay(500);
  }
  return getOccupiedPorts().length === 0;
}

function waitForExit(child: ChildProcessWithoutNullStreams): Promise<void> {
  return new Promise(resolve => {
    if (child.exitCode !== null) {
      resolve();
      return;
    }
    child.once('exit', () => resolve());
  });
}

function sendSignal(pid: number | undefined, signal: 'SIGINT' | 'SIGTERM' | 'SIGKILL'): void {
  if (!pid) {
    return;
  }
  try {
    process.kill(pid, signal);
  } catch (error) {
    if (!(error instanceof Error && 'code' in error && error.code === 'ESRCH')) {
      throw error;
    }
  }
}

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}
