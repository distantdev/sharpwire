#!/usr/bin/env bash
set -euo pipefail

DOTNET_CHANNEL="${DOTNET_CHANNEL:-10.0}"
DOTNET_ROOT_DIR="${DOTNET_ROOT:-$HOME/.dotnet}"
INSTALL_SCRIPT_PATH="/tmp/dotnet-install.sh"

mkdir -p "$DOTNET_ROOT_DIR"

ensure_line_in_file() {
  local file_path="$1"
  local line="$2"

  touch "$file_path"
  if ! rg -F -x "$line" "$file_path" >/dev/null 2>&1; then
    printf '%s\n' "$line" >> "$file_path"
  fi
}

has_dotnet_10_sdk() {
  [ -x "$DOTNET_ROOT_DIR/dotnet" ] && "$DOTNET_ROOT_DIR/dotnet" --list-sdks | rg -q "^10\\."
}

if ! has_dotnet_10_sdk; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_SCRIPT_PATH"
  bash "$INSTALL_SCRIPT_PATH" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_ROOT_DIR"
fi

case ":$PATH:" in
  *":$DOTNET_ROOT_DIR:"*) ;;
  *) export PATH="$DOTNET_ROOT_DIR:$PATH" ;;
esac

ensure_line_in_file "$HOME/.profile" 'export DOTNET_ROOT="$HOME/.dotnet"'
ensure_line_in_file "$HOME/.profile" 'export PATH="$DOTNET_ROOT:$PATH"'
ensure_line_in_file "$HOME/.bashrc" 'export DOTNET_ROOT="$HOME/.dotnet"'
ensure_line_in_file "$HOME/.bashrc" 'export PATH="$DOTNET_ROOT:$PATH"'

dotnet --info >/dev/null
dotnet restore Sharpwire.csproj
