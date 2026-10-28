#!/usr/bin/env bash
set -euo pipefail

# Awesome IMAP MCP — single-binary installer
# Usage: curl -fsSL https://raw.githubusercontent.com/shahabmokhtari/awesome-imap-mcp/main/install.sh | bash

REPO="shahabmokhtari/awesome-imap-mcp"
INSTALL_DIR="${INSTALL_DIR:-/usr/local/bin}"
TAG="${TAG:-snapshot}"

detect_platform() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$os" in
        Linux)  os="linux" ;;
        Darwin) os="osx" ;;
        *)      echo "Unsupported OS: $os" >&2; exit 1 ;;
    esac

    case "$arch" in
        x86_64|amd64) arch="x64" ;;
        aarch64|arm64) arch="arm64" ;;
        *)             echo "Unsupported architecture: $arch" >&2; exit 1 ;;
    esac

    echo "${os}-${arch}"
}

fetch() {
    # fetch URL DEST
    if command -v curl &>/dev/null; then
        curl -fsSL "$1" -o "$2"
    elif command -v wget &>/dev/null; then
        wget -q "$1" -O "$2"
    else
        echo "Error: curl or wget is required" >&2
        exit 1
    fi
}

sha256_of() {
    if command -v sha256sum &>/dev/null; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

main() {
    local platform binary base expected actual

    platform="$(detect_platform)"
    binary="awesome-imap-mcp-${platform}"

    echo "Detected platform: ${platform}"
    echo "Downloading ${binary} (${TAG})..."

    base="https://github.com/${REPO}/releases/download/${TAG}"

    # Private temp dir: a fixed /tmp path can be pre-created or symlinked by
    # another local user, and would race with concurrent installs.
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT

    fetch "${base}/${binary}" "${tmp}/${binary}"

    # Verify the download against the release's published SHA-256 sums before
    # anything is made executable or moved onto PATH. Fail closed.
    if [ "${AIMAP_INSECURE_SKIP_VERIFY:-0}" = "1" ]; then
        echo "WARNING: AIMAP_INSECURE_SKIP_VERIFY=1 — installing WITHOUT checksum verification." >&2
    else
        if ! fetch "${base}/SHA256SUMS" "${tmp}/SHA256SUMS" 2>/dev/null; then
            echo "Error: release ${TAG} publishes no SHA256SUMS, so the download cannot be verified." >&2
            echo "       Set AIMAP_INSECURE_SKIP_VERIFY=1 to install anyway (not recommended)." >&2
            exit 1
        fi
        expected="$(awk -v f="$binary" '$2 == f || $2 == "*"f {print $1}' "${tmp}/SHA256SUMS")"
        if [ -z "$expected" ]; then
            echo "Error: ${binary} is not listed in SHA256SUMS for ${TAG}." >&2
            exit 1
        fi
        actual="$(sha256_of "${tmp}/${binary}")"
        if [ "$expected" != "$actual" ]; then
            echo "Error: checksum mismatch for ${binary}" >&2
            echo "  expected ${expected}" >&2
            echo "  actual   ${actual}" >&2
            exit 1
        fi
        echo "Checksum verified (sha256 ${actual})"
    fi

    chmod +x "${tmp}/${binary}"

    if [ -w "$INSTALL_DIR" ]; then
        mv "${tmp}/${binary}" "$INSTALL_DIR/awesome-imap-mcp"
    else
        echo "Installing to ${INSTALL_DIR} (requires sudo)..."
        sudo mv "${tmp}/${binary}" "$INSTALL_DIR/awesome-imap-mcp"
    fi

    echo ""
    echo "Installed awesome-imap-mcp to ${INSTALL_DIR}/awesome-imap-mcp"
    echo ""
    echo "Quick start:"
    echo "  awesome-imap-mcp --config ./config.json"
    echo ""
    echo "Add to Claude Code:"
    echo "  claude mcp add awesome-imap-mcp -- awesome-imap-mcp"
}

main
