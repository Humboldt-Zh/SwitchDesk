#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
: "${DEVKITPRO:?Use a devkitPro Switch development environment}"
python_command=${SWITCHDESK_PYTHON:-python3}
if command -v cygpath >/dev/null 2>&1 && [[ "$python_command" == *:* ]]; then python_command=$(cygpath -u "$python_command"); fi
"$python_command" scripts/prepare-companion.py
if [[ "${SWITCHDESK_SKIP_SDK_BUILD:-0}" != 1 ]]; then
  sdk_args=(--build)
  if [[ -n "${SWITCHDESK_SDK_SOURCE_ROOT:-}" ]]; then sdk_args+=(--source-root "$SWITCHDESK_SDK_SOURCE_ROOT"); fi
  "$python_command" scripts/prepare-sdk.py "${sdk_args[@]}"
fi
version=$("$python_command" -c 'import json;print(json.load(open("build-info.json"))["version"])')
identity=$("$python_command" -c 'import json;print(json.load(open("build-info.json"))["sourceId"])')
export SOURCE_DATE_EPOCH=$("$python_command" -c 'import json;print(json.load(open("version.json"))["sourceDateEpoch"])')
build_directory=${SWITCHDESK_BUILD_DIR:-vendor/build}
cmake -S vendor/sphaira -B "$build_directory" -C vendor/dependencies.cmake -G Ninja \
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5 -DCMAKE_BUILD_TYPE=MinSizeRel -Dsphaira_VERSION_OVERRIDE="SwitchDesk-${version}+${identity}" \
  -DSWITCHDESK_SOURCE_ID="$identity" \
  -DENABLE_NVJPG=OFF -DENABLE_NTFS=OFF -DENABLE_EXT4=OFF \
  -DENABLE_LIBUSBHSFS=OFF -DENABLE_LIBUSBDVD=OFF -DENABLE_FTPSRV=OFF -DENABLE_LIBHAZE=OFF \
  -DENABLE_DEVOPTAB_NFS=OFF -DENABLE_DEVOPTAB_SMB2=OFF -DENABLE_DEVOPTAB_SFTP=OFF
cmake --build "$build_directory" --parallel "${SWITCHDESK_BUILD_JOBS:-4}"
mkdir -p artifacts/Switch/switch/SwitchDesk
cp "$build_directory/sphaira.nro" artifacts/Switch/switch/SwitchDesk/SwitchDesk.nro
cp build-info.json artifacts/Switch/
