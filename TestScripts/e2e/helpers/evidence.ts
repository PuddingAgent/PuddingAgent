import { Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

export interface E2eEvidence {
  testName: string;
  status: 'passed' | 'failed';
  baseUrl: string;
  workspaceId?: string;
  sessionId?: string;
  traceId?: string;
  runId?: string;
  screenshotPath?: string;
  browserTracePath?: string;
  backendTimelinePath?: string;
  consoleLogPath?: string;
  error?: string;
}

export async function collectEvidence(
  page: Page,
  testName: string,
  status: 'passed' | 'failed',
  extra: Partial<E2eEvidence> = {}
): Promise<E2eEvidence> {
  const artifactsDir = path.resolve('artifacts', testName);
  fs.mkdirSync(artifactsDir, { recursive: true });

  const evidence: E2eEvidence = {
    testName,
    status,
    baseUrl: page.context().browser() ? 'http://localhost:5000' : '',
    ...extra,
  };

  try {
    const screenshot = await page.screenshot({ fullPage: true });
    const screenshotPath = path.join(artifactsDir, `${status === 'failed' ? 'failure' : 'final'}.png`);
    fs.writeFileSync(screenshotPath, screenshot);
    evidence.screenshotPath = screenshotPath;
  } catch {}

  return evidence;
}

export function saveEvidenceJson(evidence: E2eEvidence): void {
  const dir = path.resolve('artifacts', evidence.testName);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    path.join(dir, 'evidence.json'),
    JSON.stringify(evidence, null, 2)
  );
}
