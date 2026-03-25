#!/usr/bin/env bash
set -euo pipefail

# Ultimate IMAP MCP — single-binary installer
# Usage: curl -fsSL https://raw.githubusercontent.com/shahab1363/ultimate-imap-mcp/main/install.sh | bash

REPO="shahab1363/ultimate-imap-mcp"
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
    binary="ultimate-imap-mcp-${platform}"

    echo "Detected platform: ${platform}"
    echo "Downloading ${binary} (${TAG})..."

    url="https://github.com/${REPO}/releases/download/${TAG}/${binary}"

    if command -v curl &>/dev/null; then
        curl -fSL "$url" -o /tmp/ultimate-imap-mcp
    elif command -v wget &>/dev/null; then
        wget -q "$url" -O /tmp/ultimate-imap-mcp
    else
        echo "Error: curl or wget is required" >&2
        exit 1
    fi

    chmod +x /tmp/ultimate-imap-mcp

    if [ -w "$INSTALL_DIR" ]; then
        mv /tmp/ultimate-imap-mcp "$INSTALL_DIR/ultimate-imap-mcp"
    else
        echo "Installing to ${INSTALL_DIR} (requires sudo)..."
        sudo mv /tmp/ultimate-imap-mcp "$INSTALL_DIR/ultimate-imap-mcp"
    fi

    echo ""
    echo "Installed ultimate-imap-mcp to ${INSTALL_DIR}/ultimate-imap-mcp"
    echo ""
    echo "Quick start:"
    echo "  ultimate-imap-mcp --config ./config.json"
    echo ""
    echo "Add to Claude Code:"
    echo "  claude mcp add ultimate-imap-mcp -- ultimate-imap-mcp"
}

main
