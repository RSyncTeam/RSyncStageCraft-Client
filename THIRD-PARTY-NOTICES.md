# Third-party notices

This file lists the third-party material in this repository and in the plugin
package built from it, with its licence and notice. The NuGet packages are not
copied into the repository; they are restored at build time, pinned by the
`packages.lock.json` files.

| Component | Version | Licence | Where it is used |
|---|---|---|---|
| `BNpcLink.csv` from LuminaSupplemental | commit `2a04329e5fb3ca6f22bc8d1915fe0d1382d59519` | GPL-3.0 | `src/RSyncStageCraft/Data/BNpcLink.csv`, embedded in the plugin DLL |
| `Glamourer.Api` | 2.8.2 | MIT | the plugin; the DLL ships in the plugin package |
| `Penumbra.Api` | 5.15.1 | MIT | the plugin; the DLL ships in the plugin package |
| Dalamud SamplePlugin template (goatcorp) | | AGPL-3.0 | the starting point of the plugin |

---

## BNpcLink.csv (LuminaSupplemental): GPL-3.0

LuminaSupplemental by Critical-Impact and contributors,
https://github.com/Critical-Impact/LuminaSupplemental, file
`src/LuminaSupplemental.Excel/Generated/BNpcLink.csv` at commit
`2a04329e5fb3ca6f22bc8d1915fe0d1382d59519`, unmodified apart from comment
lines. It is licensed under the GNU General Public License, version 3; the full
text is in `src/RSyncStageCraft/Data/BNpcLink.LICENSE`, and the plugin package
carries it as `BNpcLink.LICENSE`.

## Glamourer.Api 2.8.2: MIT

Reconstructed notice, not a verbatim upstream licence file. The package
declares the licence expression `MIT`, the authors `Ottermandias` and the
copyright `Copyright © 2025`, and contains no licence file. The holder name
below is taken from the authors field, because the copyright field names none.

```
MIT License

Copyright © 2025 Ottermandias

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

## Penumbra.Api 5.15.1: MIT

Reconstructed notice, not a verbatim upstream licence file. The package
declares the licence expression `MIT`, the authors `Ottermandias` and the
copyright `Copyright © 2025`, and contains no licence file. The holder name
below is taken from the authors field, because the copyright field names none.

```
MIT License

Copyright © 2025 Ottermandias

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

## Dalamud SamplePlugin template (goatcorp): AGPL-3.0

This project was created from the Dalamud SamplePlugin template (goatcorp),
licensed under AGPL-3.0.

Its descendants in this repository are `src/RSyncStageCraft/Plugin.cs`,
`src/RSyncStageCraft/Configuration.cs`, the plugin project file
`src/RSyncStageCraft/RSyncStageCraft.csproj`, `.editorconfig` and the workflow
`.github/workflows/build.yml`. The licence text is in `LICENSE`.
