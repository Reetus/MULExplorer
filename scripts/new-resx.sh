#!/usr/bin/env bash
# Writes a .resx from "Key=Value" lines on stdin.  Usage: scripts/new-resx.sh MULExplorer/Resources/FooStrings.resx < pairs.txt
set -euo pipefail
out="$1"
{
cat <<'HEAD'
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
HEAD
while IFS= read -r line || [ -n "$line" ]; do
  [ -z "$line" ] && continue
  case "$line" in \#*) continue;; esac
  key="${line%%=*}"; val="${line#*=}"
  val="${val//&/&amp;}"; val="${val//</&lt;}"; val="${val//>/&gt;}"
  printf '  <data name="%s" xml:space="preserve"><value>%s</value></data>\n' "$key" "$val"
done
echo '</root>'
} > "$out"
