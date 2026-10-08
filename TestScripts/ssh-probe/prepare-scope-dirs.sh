#!/usr/bin/env bash
# PuddingSsh A4 夹具：两类 `.ssh` 形态的目录 + known_hosts 变体（撤销 / 换 key）。
#
# 设计 §6.1 的两种身份来源在**组件侧**的差别是：目录与文件名由引用方冻结后传入，组件
# 只读给定的那一个目录、不搜寻其他身份、不跨作用域回退。这个脚本把两种形态做成真实目录：
#   scope-a/  「current_user 风格」：一把客户端密钥 + 真实 ssh-keyscan 记录
#   scope-b/  「agent_private 风格」：**另一把**客户端密钥 + 自己的 known_hosts
#   scope-b-rotated/known_hosts：同一主机名记录了**别的**主机公钥（换 key ⇒ mismatch）
# 两把公钥都装进夹具 authorized_keys，因此「用哪把密钥」可由服务端日志区分。
set -euo pipefail

OUT="${1:?usage: prepare-scope-dirs.sh <wsl-out-dir> [port]}"
PORT="${2:-22122}"
ROOT=/tmp/pudding-ssh-probe

[ -f "$ROOT/hostkey_ed25519" ] || { echo "missing fixture keys; run setup-wsl-sshd.sh first" >&2; exit 1; }
mkdir -p "$OUT"

# scope-b 的客户端密钥（一次性）
if [ ! -f "$ROOT/id_scope_b" ]; then
  ssh-keygen -q -t ed25519 -N "" -f "$ROOT/id_scope_b" -C pudding-scope-b
fi

# 两把公钥都授权（幂等）
grep -qF "$(cat "$ROOT/id_ed25519.pub")" "$ROOT/authorized_keys" || cat "$ROOT/id_ed25519.pub" >> "$ROOT/authorized_keys"
grep -qF "$(cat "$ROOT/id_scope_b.pub")" "$ROOT/authorized_keys" || cat "$ROOT/id_scope_b.pub" >> "$ROOT/authorized_keys"

mkdir -p "$OUT/scope-a" "$OUT/scope-b" "$OUT/scope-b-rotated"
cp "$ROOT/id_ed25519" "$OUT/scope-a/id_ed25519"
cp "$ROOT/id_scope_b" "$OUT/scope-b/id_ed25519"

# 真实 known_hosts 记录（非 22 端口 ⇒ [host]:port）
ssh-keyscan -T 5 -p "$PORT" 127.0.0.1 2>/dev/null | grep -v '^#' | sort -u > "$OUT/scope-a/known_hosts"
cp "$OUT/scope-a/known_hosts" "$OUT/scope-b/known_hosts"

# 哈希主机记录：OpenSSH 自己的 ssh-keygen -H（|1|salt|hash）
cp "$OUT/scope-a/known_hosts" "$OUT/scope-a/known_hosts.hashed"
ssh-keygen -q -H -f "$OUT/scope-a/known_hosts.hashed"
rm -f "$OUT/scope-a/known_hosts.hashed.old"

# 换 key 变体：同一主机名，记录一把**与服务器无关**的 ed25519 公钥
if [ ! -f "$ROOT/rotation_decoy" ]; then
  ssh-keygen -q -t ed25519 -N "" -f "$ROOT/rotation_decoy" -C pudding-rotation-decoy
fi
printf '[127.0.0.1]:%s ssh-ed25519 %s\n' "$PORT" "$(awk '{print $2}' "$ROOT/rotation_decoy.pub")" \
  > "$OUT/scope-b-rotated/known_hosts"

echo "scope_a_dir=$OUT/scope-a"
echo "scope_b_dir=$OUT/scope-b"
echo "scope_b_rotated_known_hosts=$OUT/scope-b-rotated/known_hosts"
echo "scope_a_hashed_known_hosts=$OUT/scope-a/known_hosts.hashed"
echo -n "scope_a_client_fp="; ssh-keygen -lf "$ROOT/id_ed25519.pub" -E sha256 | awk '{print $2}'
echo -n "scope_b_client_fp="; ssh-keygen -lf "$ROOT/id_scope_b.pub" -E sha256 | awk '{print $2}'
echo -n "scope_b_rotated_known_hosts_content="; cat "$OUT/scope-b-rotated/known_hosts"
