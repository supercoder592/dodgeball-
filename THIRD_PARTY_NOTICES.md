# Third-party notices

Dodgeball Ultra's own code is in this repository. The realistic human characters and their motion-capture
animations are **not committed**; the Setup Wizard (or `Tools/fetch_rocketbox.py`) downloads them on your machine
from the pinned commit listed in `Tools/rocketbox_manifest.json`.

## Microsoft Rocketbox Avatar Library

* Source: https://github.com/microsoft/Microsoft-Rocketbox (commit `0943055db6ec570bcef9f2c8b41c9e5467c808f9`)
* Used for: the ten heroes' rigged, textured human models and the idle / walk / run / crouch / cheer
  motion-capture clips.
* Citation (requested by the authors for research use): M. Gonzalez-Franco et al., "The Rocketbox library and the
  utility of freely available rigged avatars", *Frontiers in Virtual Reality*, 2020. DOI 10.3389/frvir.2020.561558

```
MIT License

Copyright (c) 2020 Microsoft

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Build-time tools only (not shipped with the game)

* `Tools/CompileCheck` downloads Unity reference assemblies (`UnityEngine.Modules`, `Unity3D.SDK`) from NuGet and
  builds a reference copy of Unity's uGUI (Unity Companion License) purely to type-check the C# code. Nothing from
  these packages is committed or redistributed.
* NUnit (MIT) for the rules tests.
