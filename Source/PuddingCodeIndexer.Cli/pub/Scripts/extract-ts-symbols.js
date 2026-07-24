'use strict';

// extract-ts-symbols.js
// -----------------------------------------------------------------------------
// Extracts symbol declarations and cross-references from TypeScript files using
// the TypeScript compiler API (ts.createSourceFile + ts.forEachChild).
//
// Two modes:
//
//   Single-file mode (existing):
//     node extract-ts-symbols.js <file.ts>
//     Output: { "file", "symbols", "references" }
//
//   Project mode (new):
//     node extract-ts-symbols.js --project <directory>
//     Output: { "files": [...], "crossReferences": [...] }
//
// If the `typescript` module cannot be resolved, the script prints
// { "error": "typescript not available" } and exits with code 1.
// -----------------------------------------------------------------------------

let ts;
try {
  ts = require('typescript');
} catch (e) {
  process.stdout.write(JSON.stringify({ error: 'typescript not available' }) + '\n');
  process.exit(1);
}

const fs = require('fs');
const path = require('path');

function fail(message) {
  process.stdout.write(JSON.stringify({ error: message }) + '\n');
  process.exit(1);
}

// ── Per-file extraction ─────────────────────────────────────────────────────

function extractFile(filePath) {
  let sourceText;
  try {
    sourceText = fs.readFileSync(filePath, 'utf8');
  } catch (e) {
    return { file: filePath, symbols: [], references: [], imports: [], exports: [], error: 'cannot read file: ' + filePath + ' (' + e.message + ')' };
  }

  const symbols = [];
  const references = [];
  const imports = [];
  const exports = [];
  const seenRefs = new Set();

  let sourceFile;
  try {
    sourceFile = ts.createSourceFile(
      filePath,
      sourceText,
      ts.ScriptTarget.Latest,
      /* setParentNodes */ true,
      ts.ScriptKind.TS
    );
  } catch (e) {
    return { file: filePath, symbols: [], references: [], imports: [], exports: [], error: 'failed to parse file: ' + filePath + ' (' + e.message + ')' };
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

  function lineOf(pos) {
    return sourceFile.getLineAndCharacterOfPosition(pos).line + 1;
  }

  function collapse(text) {
    return text.replace(/\s+/g, ' ').trim();
  }

  function getModifiersSafe(node) {
    if (typeof ts.getModifiers === 'function') {
      try {
        return ts.getModifiers(node) || undefined;
      } catch (e) {
        return node.modifiers;
      }
    }
    return node.modifiers;
  }

  function collectModifiers(node) {
    let target = node;
    if (
      ts.isVariableDeclaration(node) &&
      node.parent && ts.isVariableDeclarationList(node.parent) &&
      node.parent.parent && ts.isVariableStatement(node.parent.parent)
    ) {
      target = node.parent.parent;
    }

    const mods = [];
    const modifiers = getModifiersSafe(target);
    if (modifiers) {
      for (const m of modifiers) {
        switch (m.kind) {
          case ts.SyntaxKind.PublicKeyword: mods.push('public'); break;
          case ts.SyntaxKind.PrivateKeyword: mods.push('private'); break;
          case ts.SyntaxKind.ProtectedKeyword: mods.push('protected'); break;
          case ts.SyntaxKind.StaticKeyword: mods.push('static'); break;
          case ts.SyntaxKind.ExportKeyword: mods.push('export'); break;
          case ts.SyntaxKind.AbstractKeyword: mods.push('abstract'); break;
          case ts.SyntaxKind.ReadonlyKeyword: mods.push('readonly'); break;
          case ts.SyntaxKind.AsyncKeyword: mods.push('async'); break;
          case ts.SyntaxKind.DeclareKeyword: mods.push('declare'); break;
          case ts.SyntaxKind.DefaultKeyword: mods.push('default'); break;
          default: break;
        }
      }
    }
    return mods.join(',');
  }

  function signatureOf(node) {
    let text;
    const body = node.body;
    if (body && typeof body.getStart === 'function') {
      const start = node.getStart(sourceFile);
      const bodyStart = body.getStart(sourceFile);
      text = sourceText.substring(start, bodyStart);
    } else {
      text = node.getText(sourceFile);
    }
    return collapse(text).replace(/;$/, '').trim();
  }

  function qualifiedName(container, name) {
    return container ? container + '.' + name : name;
  }

  function addSymbol(kind, name, fullName, line, signature, modifiers, containerName) {
    symbols.push({
      kind: kind,
      name: name,
      fullName: fullName,
      line: line,
      signature: signature,
      modifiers: modifiers,
      containerName: containerName
    });
  }

  function expressionName(expr) {
    if (!expr) return '';
    if (ts.isIdentifier(expr)) return expr.text;
    if (ts.isPropertyAccessExpression(expr)) {
      const left = expressionName(expr.expression);
      return left ? left + '.' + expr.name.text : expr.name.text;
    }
    const t = collapse(expr.getText(sourceFile));
    return t.length > 120 ? t.substring(0, 120) : t;
  }

  function addReference(name, line, kind) {
    if (!name) return;
    const key = name + '|' + line + '|' + kind;
    if (seenRefs.has(key)) return;
    seenRefs.add(key);
    references.push({ name: name, line: line, kind: kind });
  }

  function declarationName(nameNode) {
    if (!nameNode) return '(anonymous)';
    if (ts.isIdentifier(nameNode)) return nameNode.text;
    return collapse(nameNode.getText(sourceFile));
  }

  // ── Import extraction ────────────────────────────────────────────────────

  function extractImports(node) {
    if (ts.isImportDeclaration(node) && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier)) {
      const from = node.moduleSpecifier.text;
      const names = [];

      if (node.importClause) {
        // Default import: import X from './mod'
        if (node.importClause.name) {
          names.push(node.importClause.name.text);
        }
        // Named imports: import { A, B } from './mod'
        if (node.importClause.namedBindings) {
          if (ts.isNamedImports(node.importClause.namedBindings)) {
            for (const el of node.importClause.namedBindings.elements) {
              names.push(el.name.text);
            }
          } else if (ts.isNamespaceImport(node.importClause.namedBindings)) {
            // import * as X from './mod'
            names.push(node.importClause.namedBindings.name.text);
          }
        }
      }

      if (names.length > 0) {
        imports.push({ from: from, names: names, resolvedFile: null });
      }
    }
  }

  // ── Export extraction ────────────────────────────────────────────────────

  function extractExports(node) {
    const mods = getModifiersSafe(node);
    const hasExport = mods && mods.some(function (m) { return m.kind === ts.SyntaxKind.ExportKeyword; });
    const hasDefault = mods && mods.some(function (m) { return m.kind === ts.SyntaxKind.DefaultKeyword; });

    if (!hasExport) return;

    if (ts.isFunctionDeclaration(node) || ts.isClassDeclaration(node) ||
        ts.isInterfaceDeclaration(node) || ts.isEnumDeclaration(node) ||
        ts.isTypeAliasDeclaration(node)) {
      if (node.name && ts.isIdentifier(node.name)) {
        exports.push(node.name.text);
      } else if (hasDefault) {
        exports.push('default');
      }
    } else if (ts.isVariableStatement(node)) {
      for (const decl of node.declarationList.declarations) {
        if (ts.isIdentifier(decl.name)) {
          exports.push(decl.name.text);
        }
      }
    } else if (ts.isExportDeclaration(node) && node.exportClause && ts.isNamedExports(node.exportClause)) {
      // export { A, B }
      for (const el of node.exportClause.elements) {
        exports.push(el.name.text);
      }
    } else if (ts.isExportAssignment(node)) {
      // export default expr
      exports.push('default');
    }
  }

  // ── Reference extraction (per node) ──────────────────────────────────────

  function collectReferences(node) {
    if (ts.isCallExpression(node)) {
      addReference(expressionName(node.expression), lineOf(node.getStart(sourceFile)), 'call');
    } else if (ts.isNewExpression(node)) {
      addReference(expressionName(node.expression), lineOf(node.getStart(sourceFile)), 'new');
    } else if (ts.isHeritageClause(node)) {
      const kind = node.token === ts.SyntaxKind.ExtendsKeyword ? 'extends' : 'implements';
      if (node.types) {
        for (const t of node.types) {
          addReference(expressionName(t.expression), lineOf(t.getStart(sourceFile)), kind);
        }
      }
    } else if (ts.isTypeReferenceNode(node)) {
      addReference(collapse(node.typeName.getText(sourceFile)), lineOf(node.getStart(sourceFile)), 'type_ref');
    }
  }

  // ── AST walk ─────────────────────────────────────────────────────────────

  function visit(node, container) {
    let childContainer = container;

    switch (node.kind) {
      case ts.SyntaxKind.ClassDeclaration:
      case ts.SyntaxKind.InterfaceDeclaration: {
        const kind = node.kind === ts.SyntaxKind.ClassDeclaration ? 'class' : 'interface';
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol(kind, name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        childContainer = full;
        break;
      }
      case ts.SyntaxKind.EnumDeclaration: {
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('enum', name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        childContainer = full;
        break;
      }
      case ts.SyntaxKind.TypeAliasDeclaration: {
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('type', name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        break;
      }
      case ts.SyntaxKind.FunctionDeclaration: {
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('function', name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        break;
      }
      case ts.SyntaxKind.MethodDeclaration:
      case ts.SyntaxKind.MethodSignature:
      case ts.SyntaxKind.ConstructorDeclaration: {
        const isCtor = node.kind === ts.SyntaxKind.ConstructorDeclaration;
        const name = isCtor ? 'constructor' : declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('method', name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        break;
      }
      case ts.SyntaxKind.PropertyDeclaration:
      case ts.SyntaxKind.PropertySignature:
      case ts.SyntaxKind.GetAccessorDeclaration:
      case ts.SyntaxKind.SetAccessorDeclaration: {
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('property', name, full, lineOf(node.getStart(sourceFile)),
          signatureOf(node), collectModifiers(node), container || '');
        break;
      }
      case ts.SyntaxKind.VariableDeclaration: {
        const name = declarationName(node.name);
        const full = qualifiedName(container, name);
        addSymbol('variable', name, full, lineOf(node.getStart(sourceFile)),
          collapse(node.getText(sourceFile)), collectModifiers(node), container || '');
        break;
      }
      default:
        break;
    }

    collectReferences(node);
    extractImports(node);
    extractExports(node);

    ts.forEachChild(node, function (child) {
      visit(child, childContainer);
    });
  }

  try {
    visit(sourceFile, '');
  } catch (e) {
    return { file: filePath, symbols: [], references: [], imports: [], exports: [], error: 'failed to analyze file: ' + filePath + ' (' + e.message + ')' };
  }

  return {
    file: filePath,
    symbols: symbols,
    references: references,
    imports: imports,
    exports: exports
  };
}

// ── Directory scanning ──────────────────────────────────────────────────────

const EXCLUDED_DIRS = new Set(['node_modules', 'bin', 'obj', '.git', 'dist', 'build', 'coverage']);
const TS_EXTENSIONS = new Set(['.ts', '.tsx']);

function collectTsFiles(dir) {
  const result = [];
  collectTsFilesRecursive(dir, result);
  return result;
}

function collectTsFilesRecursive(dir, result) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (e) {
    return;
  }
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.has(entry.name)) {
        collectTsFilesRecursive(fullPath, result);
      }
    } else if (entry.isFile()) {
      const ext = path.extname(entry.name).toLowerCase();
      if (TS_EXTENSIONS.has(ext)) {
        result.push(fullPath);
      }
    }
  }
}

// ── Import resolution ───────────────────────────────────────────────────────

function resolveImportPath(importingFileDir, importPath, allFilePaths) {
  // Only resolve relative imports
  if (!importPath.startsWith('.')) return null;

  const basePath = path.resolve(importingFileDir, importPath);

  // Try exact match, then with extensions, then index files
  const candidates = [
    basePath,
    basePath + '.ts',
    basePath + '.tsx',
    path.join(basePath, 'index.ts'),
    path.join(basePath, 'index.tsx'),
  ];

  for (const candidate of candidates) {
    const normalized = path.normalize(candidate);
    for (const fp of allFilePaths) {
      if (path.normalize(fp) === normalized) {
        return fp;
      }
    }
  }
  return null;
}

// ── Project mode ────────────────────────────────────────────────────────────

function runProjectMode(directory) {
  const resolvedDir = path.resolve(directory);
  if (!fs.existsSync(resolvedDir) || !fs.statSync(resolvedDir).isDirectory()) {
    fail('not a directory: ' + directory);
  }

  const allFiles = collectTsFiles(resolvedDir);
  const fileResults = [];

  for (const fp of allFiles) {
    const result = extractFile(fp);
    // Use relative path from the project directory
    result.file = path.relative(resolvedDir, fp).replace(/\\/g, '/');
    fileResults.push(result);
  }

  // Resolve imports
  for (const fr of fileResults) {
    const fileDir = path.dirname(path.resolve(resolvedDir, fr.file));
    for (const imp of fr.imports) {
      const resolved = resolveImportPath(fileDir, imp.from, allFiles);
      if (resolved) {
        imp.resolvedFile = path.relative(resolvedDir, resolved).replace(/\\/g, '/');
      }
    }
  }

  // Build a map: file -> set of exported names
  const exportsByFile = new Map();
  for (const fr of fileResults) {
    exportsByFile.set(fr.file, new Set(fr.exports));
  }

  // Build a map: file -> import name -> resolved target file
  const importNameToFile = new Map(); // key: sourceFile, value: Map(importedName -> {targetFile, from})
  for (const fr of fileResults) {
    const nameMap = new Map();
    for (const imp of fr.imports) {
      if (imp.resolvedFile) {
        for (const name of imp.names) {
          nameMap.set(name, imp.resolvedFile);
        }
      }
    }
    importNameToFile.set(fr.file, nameMap);
  }

  // Build cross-references
  const crossReferences = [];
  for (const fr of fileResults) {
    const nameMap = importNameToFile.get(fr.file);
    if (!nameMap || nameMap.size === 0) continue;

    for (const ref of fr.references) {
      // Check if the reference name (or its first segment) matches an imported name
      const refName = ref.name.split('.')[0]; // handle "obj.method" -> "obj"
      const targetFile = nameMap.get(refName);
      if (targetFile) {
        // Verify the target file actually exports this name
        const targetExports = exportsByFile.get(targetFile);
        if (targetExports && (targetExports.has(refName) || targetExports.has('default'))) {
          crossReferences.push({
            sourceFile: fr.file,
            sourceLine: ref.line,
            targetFile: targetFile,
            targetName: refName,
            kind: ref.kind
          });
        }
      }
    }
  }

  // Strip error field from file results for clean output
  const cleanFiles = fileResults.map(function (fr) {
    return {
      file: fr.file,
      symbols: fr.symbols,
      references: fr.references,
      imports: fr.imports,
      exports: fr.exports
    };
  });

  return {
    files: cleanFiles,
    crossReferences: crossReferences
  };
}

// ── Main ────────────────────────────────────────────────────────────────────

const args = process.argv.slice(2);

if (args[0] === '--project') {
  const dir = args[1];
  if (!dir) {
    fail('usage: node extract-ts-symbols.js --project <directory>');
  }
  const result = runProjectMode(dir);
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
} else {
  // Single-file mode (backward compatible)
  const filePath = args[0];
  if (!filePath) {
    fail('usage: node extract-ts-symbols.js <file.ts>');
  }

  const result = extractFile(filePath);
  if (result.error) {
    fail(result.error);
  }

  // Output in the original format for backward compatibility
  const output = {
    file: filePath,
    symbols: result.symbols,
    references: result.references
  };
  process.stdout.write(JSON.stringify(output, null, 2) + '\n');
}
