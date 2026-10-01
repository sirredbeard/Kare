---
name: ssh-device-access
description: Connect to remote Linux and embedded devices over SSH from Copilot CLI, including reliable non-interactive password fallback when key authentication is not available yet.
---

# SSH device access

Use SSH as the normal path to a remote device. Start with the least clever connection that can work, identify the exact failure, then add only the option that fixes it.

## Check the path first

Confirm the host and port are reachable before debugging authentication:

```bash
timeout 5 bash -c '</dev/tcp/192.0.2.10/22'
```

Then try key authentication without allowing a password prompt:

```bash
ssh -o BatchMode=yes -o ConnectTimeout=10 user@192.0.2.10 'uname -a'
```

Read the error literally:

- `Connection refused` means SSH is not listening on that address and port.
- `No route to host` or a timeout means the network path is wrong.
- `Host key verification failed` means `known_hosts` needs attention.
- `Permission denied` means the network path worked, however, the username or authentication method did not.

Do not keep retrying a misspelled username with more flags.

## Prefer a durable host entry

Put repeat connections in `~/.ssh/config`:

```sshconfig
Host target
  HostName 192.0.2.10
  User user
  Port 22
  IdentityFile ~/.ssh/id_ed25519
  IdentitiesOnly yes
  ConnectTimeout 10
  ServerAliveInterval 30
  ServerAliveCountMax 3
```

The normal commands are then:

```bash
ssh target
ssh target 'uname -a'
scp target:/tmp/log.txt ./
```

Use `ProxyJump` for a device behind a bastion. Use `ControlMaster auto` and `ControlPersist 5m` when several commands need the same connection.

## Non-interactive password fallback

Use this only when the password is already known and key authentication is not available. OpenSSH will not necessarily read a password from redirected standard input. `SSH_ASKPASS_REQUIRE=force` plus `setsid` reliably makes it call a temporary askpass helper instead.

Prompt for the password without echoing it:

```bash
read -rsp 'SSH password: ' SSH_PASSWORD
printf '\n'
export SSH_PASSWORD

askpass="$(mktemp)"
trap 'rm -f "$askpass"; unset SSH_PASSWORD' EXIT

cat >"$askpass" <<'EOF'
#!/bin/sh
printf '%s\n' "$SSH_PASSWORD"
EOF
chmod 700 "$askpass"

DISPLAY=:0 \
SSH_ASKPASS="$askpass" \
SSH_ASKPASS_REQUIRE=force \
setsid ssh \
  -o StrictHostKeyChecking=accept-new \
  -o ConnectTimeout=10 \
  user@192.0.2.10 'uname -a'
```

The important pieces are:

- The secret is supplied through the helper, not a command-line argument.
- `setsid` removes the controlling terminal so OpenSSH uses askpass.
- `StrictHostKeyChecking=accept-new` accepts a new host key but still rejects a changed one.
- The helper is mode `0700` and removed when the command finishes.

If a secure runtime already supplies the password as an environment variable, skip the `read` line. Never place the password literal in a command, skill, source file, commit, process argument, or log.

`sshpass -e` is a reasonable fallback when it is already installed. Do not install another package for one connection when the OpenSSH askpass path works.

## Commands that need a terminal

Add `-tt` only when the remote program genuinely needs a TTY, such as an interactive installer or `sudo` prompt:

```bash
ssh -tt target 'sudo systemctl status example'
```

Do not use `-tt` by default. It can change buffering, signal handling, and whether a command waits for input.

## Make the next connection boring

Once access works, install a public key:

```bash
ssh-copy-id target
```

Verify a second connection with `BatchMode=yes`, then stop using the password path. Stable host aliases and key authentication are the whole trick.
