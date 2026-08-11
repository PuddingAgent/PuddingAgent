import sqlite3, os, time

db_path = r"D:\data\databases\pudding_platform.db"
print(f"Before VACUUM: {os.path.getsize(db_path) / 1024 / 1024 / 1024:.2f} GB")
t0 = time.time()
conn = sqlite3.connect(db_path)
conn.execute("VACUUM")
conn.close()
t1 = time.time()
print(f"After VACUUM:  {os.path.getsize(db_path) / 1024 / 1024 / 1024:.2f} GB")
print(f"Time: {t1 - t0:.1f}s")
