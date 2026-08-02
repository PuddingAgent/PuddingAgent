import csv
import json
from pathlib import Path

cfg = json.loads(Path("exports/customers.json").read_text(encoding="utf-8"))
rows = cfg["rows"]

with Path("exports/output.csv").open("w", newline="", encoding="utf-8") as f:
    writer = csv.DictWriter(f, fieldnames=["id", "name", "email"])
    writer.writeheader()
    for row in rows:
        writer.writerow({
            "id": row["id"],
            "name": row["name"],
            "email": row["email_address"],
        })

print("exported", len(rows))
