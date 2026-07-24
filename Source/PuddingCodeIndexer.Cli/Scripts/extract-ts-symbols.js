'use strict';

// extract-ts-symbols.js
// -----------------------------------------------------------------------------
// Extracts symbol declarations and cross-references from a TypeScript file using
// the TypeScript compiler API (ts.createSourceFile + ts.forEachChild).
//
// Intended to be invoked by the .NET TypeScriptIndexer as a child process:
//
//     node extract-ts-symbols.js <file.ts>
//
// Output: a single JSON document written to stdout:
//
//     {
//       "file": "<path>",
//       "symbols": [
//         { "kind", "name", "fullName", "line", "signature", "modifiers", "containerName" }
//       ],
//       "references": [
//         { "name", "line", "kind" }   // kind: call|new|extends|implements|type_ref
//       ]
//     }
//
// If the `typescript` module cannot be resolved, the script prints
// { "error": "typescript not available" } and exits with code 1.
// No `npm install` is performed by this script.
// -----------------------------------------------------------------------------

let ts;
try {
  ts = require('typescript');
} catch (e) {
  process.stdout.write(JSON.stringify({ error: 'typescript not available' }) + '\n');
  process.exit(1);
}

const fs = require('fs');

function fail(message) {
  process.stdout.write(JSON.stringify({ error: message }) + '\n');
  process.exit(1);
}

const filePath = process.argv[2];
if (!filePath) {
  fail('usage: node extract-ts-symbols.js <file.ts>');
}

let sourceText;
try {
  sourceText = fs.readFileSync(filePath, 'utf8');
} catch (e) {
  fail('cannot read file: ' + filePath + ' (' + e.message + ')');
}

const symbols = [];
const references = [];
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
  fail('failed to parse file: ' + filePath + ' (' + e.message + ')');
}

// ── Helpers ──────────────────────────────────────────────────────────────────

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

// Collect the modifier keywords that matter to the consumer.
function collectModifiers(node) {
  // For variable declarations the modifiers live on the enclosing VariableStatement.
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

// Signature = declaration text up to (but excluding) the body block, if any.
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

// ── Reference extraction (per node) ──────────────────────────────────────────

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

// ── AST walk ─────────────────────────────────────────────────────────────────

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

  ts.forEachChild(node, function (child) {
    visit(child, childContainer);
  });
}

try {
  visit(sourceFile, '');
} catch (e) {
  fail('failed to analyze file: ' + filePath + ' (' + e.message + ')');
}

const result = {
  file: filePath,
  symbols: symbols,
  references: references
};

process.stdout.write(JSON.stringify(result, null, 2) + '\n');
