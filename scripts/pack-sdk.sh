#!/usr/bin/env bash
# Empaqueta el SDK (VortexApi.Client) en un feed NuGet local para los sistemas en desarrollo.
# Cada ejecución genera una versión nueva (siguiente parche + -dev.<fecha>, p. ej. 0.3.1-dev.20261101120000),
# mayor que la última publicada y distinta cada vez, así NuGet no reutiliza la caché.
#
#   scripts/pack-sdk.sh                 # feed en ~/.nuget/vortex-local
#   VORTEX_FEED=/otra/ruta scripts/pack-sdk.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${VORTEX_FEED:-$HOME/.nuget/vortex-local}"
current="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")"
IFS=. read -r major minor patch <<< "${current%%-*}"
version="$major.$minor.$((patch + 1))-dev.$(date -u +%Y%m%d%H%M%S)"

mkdir -p "$feed"
dotnet pack "$root/src/VortexApi.Client/VortexApi.Client.csproj" \
  --configuration Release -p:Version="$version" --output "$feed" -p:IncludeSymbols=false --nologo

echo
echo "Paquete VortexApi.Client $version en $feed"
echo "En el sistema:  dotnet add package VortexApi.Client --version $version --source $feed"
