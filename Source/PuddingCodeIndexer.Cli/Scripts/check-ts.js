const ts = require('typescript');
console.log('TS version:', ts.version);
console.log('ScriptTarget type:', typeof ts.ScriptTarget);
if (ts.ScriptTarget) {
  console.log('ScriptTarget keys:', Object.keys(ts.ScriptTarget).slice(0, 10));
  console.log('Latest:', ts.ScriptTarget.Latest);
} else {
  console.log('ScriptTarget is undefined');
}
