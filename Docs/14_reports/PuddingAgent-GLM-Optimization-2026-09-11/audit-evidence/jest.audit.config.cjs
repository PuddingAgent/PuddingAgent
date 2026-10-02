const path = require('path');
const project = path.resolve(__dirname, '../../Source/PuddingPlatformAdmin');
require(path.join(project, 'node_modules/ts-node')).register({
  transpileOnly: true, compilerOptions: { module: 'commonjs', moduleResolution: 'node' },
});
module.exports = async () => {
  const base = await require(path.join(project, 'jest.config.ts')).default();
  return { ...base, rootDir: project, roots: [project, __dirname],
    modulePaths: [path.join(project, 'node_modules')],
  };
};
