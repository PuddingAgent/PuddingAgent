#!/usr/bin/env python3
"""Extract Python symbols using the ast module. Outputs JSON to stdout.

Usage:
  Single-file mode:
    python extract-py-symbols.py <file.py>
    Output: {"symbols": [...], "references": [...]}

  Project mode:
    python extract-py-symbols.py --project <directory>
    Output: {"files": [...], "crossReferences": [...]}

Output format (single-file):
{
  "symbols": [{"kind":"class|function|method|async_func","name":"...","line":N,"signature":"...","containerName":"..."}],
  "references": [{"name":"called_func","line":N,"kind":"call|import|decorator"}]
}

Output format (project):
{
  "files": [
    {
      "file": "relative/path.py",
      "symbols": [...],
      "references": [...],
      "imports": [{"from": "module", "names": ["name1"], "resolvedFile": "module.py"}],
      "exports": ["ClassName", "func_name"]
    }
  ],
  "crossReferences": [
    {"sourceFile": "a.py", "sourceLine": N, "targetFile": "b.py", "targetName": "func", "kind": "call"}
  ]
}
"""
import ast
import json
import os
import sys


EXCLUDED_DIRS = {"__pycache__", ".git", "venv", ".venv", "node_modules", "bin", "obj"}


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


def extract_imports(tree):
    """Extract import statements from an AST tree.

    Returns a list of dicts: {"from": module, "names": [imported names], "resolvedFile": None}
    """
    imports = []

    for node in ast.walk(tree):
        if isinstance(node, ast.ImportFrom):
            module = node.module or ""
            names = [alias.name for alias in node.names]
            if module and names:
                imports.append({"from": module, "names": names, "resolvedFile": None})
        elif isinstance(node, ast.Import):
            for alias in node.names:
                imports.append({"from": alias.name, "names": [alias.name], "resolvedFile": None})

    return imports


def extract_exports(tree):
    """Extract exported (top-level public) symbol names from an AST tree.

    In Python, all top-level names not starting with underscore are considered exports.
    Also includes names listed in __all__ if defined.
    """
    exports = []
    all_names = None

    for node in ast.iter_child_nodes(tree):
        # Check for __all__ assignment
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and target.id == "__all__":
                    if isinstance(node.value, (ast.List, ast.Tuple)):
                        all_names = []
                        for elt in node.value.elts:
                            if isinstance(elt, ast.Constant) and isinstance(elt.value, str):
                                all_names.append(elt.value)

        # Top-level class definitions
        if isinstance(node, ast.ClassDef):
            if not node.name.startswith("_"):
                exports.append(node.name)

        # Top-level function definitions
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            if not node.name.startswith("_"):
                exports.append(node.name)

        # Top-level variable assignments (simple names)
        if isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name) and not target.id.startswith("_"):
                    if target.id != "__all__":
                        exports.append(target.id)

    # If __all__ is defined, use it as the definitive export list
    if all_names is not None:
        return all_names

    return exports


def collect_py_files(directory):
    """Recursively collect all .py files in a directory, excluding certain dirs."""
    result = []
    _collect_py_files_recursive(directory, result)
    return result


def _collect_py_files_recursive(directory, result):
    try:
        entries = os.listdir(directory)
    except OSError:
        return

    for entry in sorted(entries):
        full_path = os.path.join(directory, entry)
        if os.path.isdir(full_path):
            if entry not in EXCLUDED_DIRS:
                _collect_py_files_recursive(full_path, result)
        elif os.path.isfile(full_path):
            if entry.endswith(".py"):
                result.append(full_path)


def resolve_import(importing_file_dir, module_name, all_file_paths, project_root):
    """Resolve a Python module name to a file path within the project.

    Tries:
      1. module_name.py in the same directory
      2. module_name.py relative to project root
      3. module_name/__init__.py relative to project root
      4. Dotted module paths (a.b -> a/b.py)
    """
    # Convert dotted module to path
    module_path = module_name.replace(".", os.sep)

    candidates = [
        os.path.join(importing_file_dir, module_path + ".py"),
        os.path.join(project_root, module_path + ".py"),
        os.path.join(project_root, module_path, "__init__.py"),
    ]

    for candidate in candidates:
        normalized = os.path.normpath(candidate)
        for fp in all_file_paths:
            if os.path.normpath(fp) == normalized:
                return fp

    return None


def run_project_mode(directory):
    """Run project-mode extraction: scan all .py files, build exports and cross-references."""
    resolved_dir = os.path.abspath(directory)
    if not os.path.isdir(resolved_dir):
        print(json.dumps({"error": f"not a directory: {directory}"}))
        sys.exit(1)

    all_files = collect_py_files(resolved_dir)
    file_results = []

    for fp in all_files:
        rel_path = os.path.relpath(fp, resolved_dir).replace("\\", "/")

        try:
            with open(fp, "r", encoding="utf-8-sig") as f:
                source = f.read()
        except (IOError, OSError) as e:
            file_results.append({
                "file": rel_path,
                "symbols": [],
                "references": [],
                "imports": [],
                "exports": [],
            })
            continue

        try:
            tree = ast.parse(source, filename=fp)
        except SyntaxError:
            file_results.append({
                "file": rel_path,
                "symbols": [],
                "references": [],
                "imports": [],
                "exports": [],
            })
            continue

        symbols, references = extract_symbols(tree, fp)
        imports = extract_imports(tree)
        exports = extract_exports(tree)

        file_results.append({
            "file": rel_path,
            "symbols": symbols,
            "references": references,
            "imports": imports,
            "exports": exports,
        })

    # Resolve imports
    for fr in file_results:
        file_dir = os.path.dirname(os.path.join(resolved_dir, fr["file"]))
        for imp in fr["imports"]:
            resolved = resolve_import(file_dir, imp["from"], all_files, resolved_dir)
            if resolved:
                imp["resolvedFile"] = os.path.relpath(resolved, resolved_dir).replace("\\", "/")

    # Build exports lookup: file -> set of exported names
    exports_by_file = {}
    for fr in file_results:
        exports_by_file[fr["file"]] = set(fr["exports"])

    # Build import name -> target file mapping per source file
    import_name_to_file = {}  # sourceFile -> {importedName -> targetFile}
    for fr in file_results:
        name_map = {}
        for imp in fr["imports"]:
            if imp["resolvedFile"]:
                for name in imp["names"]:
                    name_map[name] = imp["resolvedFile"]
        import_name_to_file[fr["file"]] = name_map

    # Build cross-references
    cross_references = []
    for fr in file_results:
        name_map = import_name_to_file.get(fr["file"], {})
        if not name_map:
            continue

        for ref in fr["references"]:
            # Check if the reference name (or its first segment) matches an imported name
            ref_name = ref["name"].split(".")[0]
            target_file = name_map.get(ref_name)
            if target_file:
                # Verify the target file actually exports this name
                target_exports = exports_by_file.get(target_file, set())
                if ref_name in target_exports:
                    cross_references.append({
                        "sourceFile": fr["file"],
                        "sourceLine": ref["line"],
                        "targetFile": target_file,
                        "targetName": ref_name,
                        "kind": ref["kind"],
                    })

    return {
        "files": file_results,
        "crossReferences": cross_references,
    }


def main():
    if len(sys.argv) < 2:
        print(json.dumps({"error": "Usage: python extract-py-symbols.py <file.py> | --project <directory>"}))
        sys.exit(1)

    if sys.argv[1] == "--project":
        if len(sys.argv) < 3:
            print(json.dumps({"error": "Usage: python extract-py-symbols.py --project <directory>"}))
            sys.exit(1)
        result = run_project_mode(sys.argv[2])
        print(json.dumps(result, ensure_ascii=False))
        return

    # Single-file mode (backward compatible)
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
