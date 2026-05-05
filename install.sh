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

main() {
    local platform binary url

    platform="$(detect_platform)"
    binary="awesome-imap-mcp-${platform}"

    echo "Detected platform: ${platform}"
    echo "Downloading ${binary} (${TAG})..."

    url="https://github.com/${REPO}/releases/download/${TAG}/${binary}"

    if command -v curl &>/dev/null; then
        curl -fSL "$url" -o /tmp/awesome-imap-mcp
    elif command -v wget &>/dev/null; then
        wget -q "$url" -O /tmp/awesome-imap-mcp
    else
        echo "Error: curl or wget is required" >&2
        exit 1
    fi

    chmod +x /tmp/awesome-imap-mcp

    if [ -w "$INSTALL_DIR" ]; then
        mv /tmp/awesome-imap-mcp "$INSTALL_DIR/awesome-imap-mcp"
    else
        echo "Installing to ${INSTALL_DIR} (requires sudo)..."
        sudo mv /tmp/awesome-imap-mcp "$INSTALL_DIR/awesome-imap-mcp"
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
