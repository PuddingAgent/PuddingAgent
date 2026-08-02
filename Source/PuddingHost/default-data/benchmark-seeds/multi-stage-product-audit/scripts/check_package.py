from pathlib import Path

required = ["docs/product-brief.md", "risk-register.md", "next-actions.md"]
missing = [item for item in required if not Path(item).exists()]
if missing:
    print("missing:", ", ".join(missing))
    raise SystemExit(1)
print("package looks complete")
