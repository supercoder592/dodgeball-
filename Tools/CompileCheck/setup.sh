#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Dodgeball Ultra - compile-check toolchain bootstrap (no Unity install/licence needed)
#
# Downloads Unity reference assemblies from NuGet and builds a reference copy of
# uGUI (UnityEngine.UI) so the game's C# can be compiled with the .NET SDK.
# This is a *compile* check: it proves the code type-checks against the real
# UnityEngine API surface (Unity 2021.3 reference assemblies + Unity 6 shims via
# PhysicsCompat). It does not run the game.
#
# Requirements: dotnet SDK 8+, curl, unzip, git.
# -----------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE="$HERE/.cache"
mkdir -p "$CACHE"
cd "$CACHE"

if [ ! -f modules/lib/net45/UnityEngine.CoreModule.dll ]; then
  echo "[setup] Downloading UnityEngine.Modules 2021.3.33 reference assemblies..."
  curl -fsSL -o modules.nupkg https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg
  rm -rf modules && mkdir modules && unzip -q -o modules.nupkg -d modules
  chmod -R u+rw modules
fi

if [ ! -f sdk/lib/UnityEditor.dll ]; then
  echo "[setup] Downloading Unity3D.SDK 2021.1.14.1 (UnityEditor reference)..."
  curl -fsSL -o sdk.nupkg https://api.nuget.org/v3-flatcontainer/unity3d.sdk/2021.1.14.1/unity3d.sdk.2021.1.14.1.nupkg
  rm -rf sdk && mkdir sdk && unzip -q -o sdk.nupkg -d sdk
  chmod -R u+rw sdk
fi

if [ ! -f nunit/lib/netstandard2.0/nunit.framework.dll ]; then
  echo "[setup] Downloading NUnit 3.13.3..."
  curl -fsSL -o nunit.nupkg https://api.nuget.org/v3-flatcontainer/nunit/3.13.3/nunit.3.13.3.nupkg
  rm -rf nunit && mkdir nunit && unzip -q -o nunit.nupkg -d nunit
  chmod -R u+rw nunit
fi

if [ ! -f ugui/bin/UnityEngine.UI.dll ]; then
  echo "[setup] Building uGUI (UnityEngine.UI) reference from Unity-Technologies/uGUI@2018.4..."
  rm -rf ugui-src && git clone -q --depth 1 -b 2018.4 https://github.com/Unity-Technologies/uGUI ugui-src
  rm -rf ugui && mkdir -p ugui/src
  cp -r ugui-src/UnityEngine.UI/EventSystem ugui-src/UnityEngine.UI/UI ugui/src/
  # Patch the two spots that differ from the 2021.3 engine API.
  sed -i 's#UnityEngineInternal.ScriptingUtils.CreateDelegate#System.Delegate.CreateDelegate#g' ugui/src/UI/Core/Utility/ReflectionMethodsCache.cs
  sed -i 's#List<Vector2> m_Uv\([0-3]\)S#List<Vector4> m_Uv\1S#; s#m_Uv\([0-3]\)S = ListPool<Vector2>#m_Uv\1S = ListPool<Vector4>#; s#ListPool<Vector2>.Release(m_Uv#ListPool<Vector4>.Release(m_Uv#' ugui/src/UI/Core/Utility/VertexHelper.cs
  sed -i 's#AddRange(\(m\.uv[0-9]*\))#AddRange(System.Linq.Enumerable.Select(\1, v => (Vector4)v))#' ugui/src/UI/Core/Utility/VertexHelper.cs
  cp "$HERE/Projects/UnityEngine.UI.csproj.template" ugui/UnityEngine.UI.csproj
  dotnet build ugui/UnityEngine.UI.csproj -c Release -nologo -v q -o ugui/bin
fi
echo "[setup] Toolchain ready in $CACHE"
