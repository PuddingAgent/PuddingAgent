/**
 * scan-ant-pro-template.cjs
 *
 * 静态扫描 Ant Design Pro 模板痕迹，阻断以下内容进入生产页面：
 * - "Ant Design Pro" 品牌名
 * - "Powered by Ant Design" / "Powered by Ant Desgin" 文案
 * - ant-design-pro GitHub 链接
 * - pro.ant.design 链接
 *
 * 使用：node scripts/scan-ant-pro-template.cjs
 * 在 package.json 中作为 lint:branding 脚本运行。
 */

const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const blocked = [
  'Ant Design Pro',
  'Powered by Ant Design',
  'Powered by Ant Desgin',
  'github.com/ant-design/ant-design-pro',
  'pro.ant.design',
];

// 允许列表：这些文件/模式可以包含上述文本
const allowList = new Set([
  path.normalize('scripts/scan-ant-pro-template.cjs'),
  path.normalize('README.md'),
  path.normalize('package.json'),
  path.normalize('config/config.ts'),
  path.normalize('config/proxy.ts'),
  path.normalize('config/oneapi.json'),
  path.normalize('src/manifest.json'),
]);

// e2e 测试文件包含模板字符串作为测试目标，允许豁免
const allowPrefixes = [
  path.normalize('e2e/'),
];

// 需要忽略的目录
const ignoredDirs = new Set(['node_modules', 'dist', '.umi', '.umi-production', '.umi-test', '.git', 'coverage']);

function walk(dir) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  return entries.flatMap((entry) => {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (ignoredDirs.has(entry.name)) return [];
      return walk(fullPath);
    }
    if (!/\.(ts|tsx|js|jsx|less|css|md|json)$/.test(entry.name)) return [];
    return [fullPath];
  });
}

const failures = [];

for (const file of walk(root)) {
  const relative = path.relative(root, file);
  if (allowList.has(path.normalize(relative))) continue;

  // 检查是否在允许前缀路径下
  const isAllowed = allowPrefixes.some((prefix) =>
    path.normalize(relative).startsWith(prefix),
  );
  if (isAllowed) continue;

  const text = fs.readFileSync(file, 'utf8');
  for (const phrase of blocked) {
    if (text.includes(phrase)) {
      failures.push(`${relative}: contains "${phrase}"`);
    }
  }
}

if (failures.length > 0) {
  console.error('Ant Design Pro branding detected in the following files:');
  console.error(failures.join('\n'));
  process.exit(1);
} else {
  console.log('✓ No Ant Design Pro branding detected.');
}
