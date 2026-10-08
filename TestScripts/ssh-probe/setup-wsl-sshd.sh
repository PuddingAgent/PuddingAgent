#!/usr/bin/env bash
# PuddingSsh 探针夹具（一次性、受控 OpenSSH 服务端）
# 仅用于测试：所有密钥与配置都在 /tmp/pudding-ssh-probe 下，不触碰系统 sshd。
set -euo pipefail

ROOT=/tmp/pudding-ssh-probe
PORT="${1:-22122}"

rm -rf "$ROOT"
mkdir -p "$ROOT" /run/sshd
chmod 700 "$ROOT"

# 服务端主机密钥（一次性）
ssh-keygen -q -t ed25519 -N "" -f "$ROOT/hostkey_ed25519" -C pudding-probe-host >/dev/null
ssh-keygen -q -t rsa -b 3072 -N "" -f "$ROOT/hostkey_rsa" -C pudding-probe-host >/dev/null

# 客户端密钥：无口令 + 加密（用于验证口令需求路径）
ssh-keygen -q -t ed25519 -N "" -f "$ROOT/id_ed25519" -C pudding-probe-client >/dev/null
ssh-keygen -q -t ed25519 -N "probe-passphrase" -f "$ROOT/id_ed25519_encrypted" -C pudding-probe-client-enc >/dev/null

cat "$ROOT/id_ed25519.pub" > "$ROOT/authorized_keys"
chmod 600 "$ROOT/authorized_keys"

cat > "$ROOT/sshd_config" <<EOF
Port $PORT
ListenAddress 127.0.0.1
Protocol 2
HostKey $ROOT/hostkey_ed25519
HostKey $ROOT/hostkey_rsa
PidFile $ROOT/sshd.pid
AuthorizedKeysFile $ROOT/authorized_keys
PermitRootLogin prohibit-password
PubkeyAuthentication yes
PasswordAuthentication no
KbdInteractiveAuthentication no
UsePAM no
StrictModes no
PrintMotd no
X11Forwarding no
AllowTcpForwarding no
LogLevel VERBOSE
EOF

/usr/sbin/sshd -t -f "$ROOT/sshd_config"
echo "config-ok"
echo "port=$PORT"
echo -n "hostkey_fp_sha256="; ssh-keygen -lf "$ROOT/hostkey_ed25519.pub" -E sha256 | awk '{print $2}'
echo -n "hostkey_alg="; ssh-keygen -lf "$ROOT/hostkey_ed25519.pub" -E sha256 | awk '{print $4}'
echo -n "os="; . /etc/os-release; echo "$PRETTY_NAME"
echo -n "sshd_version="; /usr/sbin/sshd -V 2>&1 | head -1
