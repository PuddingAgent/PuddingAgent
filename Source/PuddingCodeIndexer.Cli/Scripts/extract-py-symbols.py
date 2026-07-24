#!/usr/bin/env python3
"""Extract Python symbols using the ast module. Outputs JSON to stdout.

Usage: python extract-py-symbols.py <file.py>

Output format:
{
  "symbols": [{"kind":"class|function|method|async_func","name":"...","line":N,"signature":"...","containerName":"..."}],
  "references": [{"name":"called_func","line":N,"kind":"call|import|decorator"}]
}
"""
import ast
import json
import sys


def get_signature(node):
    """Build a signature string from a function/async function definition."""
    args = node.args
    parts = []

    # positional-only args
    for a in args.posonlyargs:
        parts.append(a.arg)
    if args.posonlyargs:
        parts.append("/")

    # regular args
    for a in args.args:
        parts.append(a.arg)

    # *args
    if args.vararg:
        parts.append(f"*{args.vararg.arg}")
    elif args.kwonlyargs:
        parts.append("*")

    # keyword-only args
    for a in args.kwonlyargs:
        parts.append(a.arg)

    # **kwargs
    if args.kwarg:
        parts.append(f"**{args.kwarg.arg}")

    sig = f"({', '.join(parts)})"

    # return annotation
    if node.returns:
        try:
            ret = ast.unparse(node.returns)
            sig += f" -> {ret}"
        except Exception:
            pass

    return sig


def extract_symbols(tree, filename):
    """Extract symbols and references from an AST tree."""
    symbols = []
    references = []

    class Visitor(ast.NodeVisitor):
        def __init__(self):
            self.container_stack = []

        @property
        def container_name(self):
            return self.container_stack[-1] if self.container_stack else ""

        def visit_ClassDef(self, node):
            symbols.append({
                "kind": "class",
                "name": node.name,
                "line": node.lineno,
                "signature": f"class {node.name}",
                "containerName": self.container_name,
            })
            # decorators as references
            for dec in node.decorator_list:
                name = self._get_call_name(dec)
                if name:
                    references.append({"name": name, "line": node.lineno, "kind": "decorator"})

            self.container_stack.append(node.name)
            self.generic_visit(node)
            self.container_stack.pop()

        def visit_FunctionDef(self, node):
            kind = "method" if self.container_name else "function"
            symbols.append({
                "kind": kind,
                "name": node.name,
                "line": node.lineno,
                "signature": f"def {node.name}{get_signature(node)}",
                "containerName": self.container_name,
            })
            # decorators as references
            for dec in node.decorator_list:
                name = self._get_call_name(dec)
                if name:
                    references.append({"name": name, "line": node.lineno, "kind": "decorator"})

            self.container_stack.append(node.name)
            self.generic_visit(node)
            self.container_stack.pop()

        def visit_AsyncFunctionDef(self, node):
            kind = "method" if self.container_name else "async_func"
            symbols.append({
                "kind": kind,
                "name": node.name,
                "line": node.lineno,
                "signature": f"async def {node.name}{get_signature(node)}",
                "containerName": self.container_name,
            })
            for dec in node.decorator_list:
                name = self._get_call_name(dec)
                if name:
                    references.append({"name": name, "line": node.lineno, "kind": "decorator"})

            self.container_stack.append(node.name)
            self.generic_visit(node)
            self.container_stack.pop()

        def visit_Call(self, node):
            name = self._get_call_name(node.func)
            if name:
                references.append({"name": name, "line": node.lineno, "kind": "call"})
            self.generic_visit(node)

        def visit_Import(self, node):
            for alias in node.names:
                references.append({"name": alias.name, "line": node.lineno, "kind": "import"})

        def visit_ImportFrom(self, node):
            module = node.module or ""
            for alias in node.names:
                full_name = f"{module}.{alias.name}" if module else alias.name
                references.append({"name": full_name, "line": node.lineno, "kind": "import"})

        def _get_call_name(self, node):
            if isinstance(node, ast.Name):
                return node.id
            elif isinstance(node, ast.Attribute):
                value_name = self._get_call_name(node.value)
                if value_name:
                    return f"{value_name}.{node.attr}"
                return node.attr
            elif isinstance(node, ast.Call):
                return self._get_call_name(node.func)
            return None

    visitor = Visitor()
    visitor.visit(tree)
    return symbols, references


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Usage: python extract-py-symbols.py <file.py>"}))
        sys.exit(1)

    filepath = sys.argv[1]

    try:
        with open(filepath, "r", encoding="utf-8-sig") as f:
            source = f.read()
    except (IOError, OSError) as e:
        print(json.dumps({"error": f"Cannot read file: {e}"}))
        sys.exit(1)

    try:
        tree = ast.parse(source, filename=filepath)
    except SyntaxError as e:
        print(json.dumps({"error": f"Syntax error: {e}"}))
        sys.exit(1)

    symbols, references = extract_symbols(tree, filepath)
    print(json.dumps({"symbols": symbols, "references": references}, ensure_ascii=False))


if __name__ == "__main__":
    main()
