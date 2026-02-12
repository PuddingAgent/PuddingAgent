import { readFileSync } from 'node:fs';
import { join } from 'node:path';

describe('React 19 AntD patch order', () => {
  it('loads the AntD React 19 patch before AntD modules', () => {
    const appSource = readFileSync(join(__dirname, 'app.tsx'), 'utf8');
    const importLines = appSource
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.startsWith('import'));

    expect(importLines[0]).toBe("import '@ant-design/v5-patch-for-react-19';");
  });
});
