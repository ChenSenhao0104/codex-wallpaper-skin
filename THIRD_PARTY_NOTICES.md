# Third-party notices

The project does not bundle Codex, Steam, Wallpaper Engine, Workshop media, Node.js, FFmpeg, or Wallpaper Engine's proprietary runtime assets. Shader files are read only from the user's own local Wallpaper Engine installation or selected scene package and are transferred in memory for rendering.

## Bundled renderer code

The application bundles modified JavaScript source from **we-scene**, pinned to commit `6b503a36b952f91dbab5e6f378f632f87baf05cc`, under the MIT License:

- Upstream project: <https://github.com/meslzy/we-scene>
- Bundled license: `companion/ThirdParty/we-scene/LICENSE`

Local changes add bounded package/texture/shader handling, browser lifecycle cleanup, WebGL resource limits, additional built-in material metadata, and HLSL-to-GLSL compatibility fixes. The portable release includes the license as `WE-SCENE-LICENSE.txt`.

## Windows SDK .NET projections

The Windows build uses Microsoft's Windows SDK .NET targeting pack and C#/WinRT runtime projection to access Windows Graphics Capture and Direct3D 11. Published packages may include `Microsoft.Windows.SDK.NET.dll` and `WinRT.Runtime.dll` from that Microsoft-provided framework pack.

- Packages: `Microsoft.Windows.SDK.NET.Ref` and `Microsoft.Windows.CsWinRT 2.1.5`
- Publisher: Microsoft Corporation
- License terms: <https://aka.ms/WinSDKLicenseURL>

## Direct3D and Media Foundation interop

The Windows build uses **Vortice.Direct3D11 3.6.2** and
**Vortice.MediaFoundation 3.6.2** to convert captured textures on the GPU and
access the operating system's Media Foundation H.264 encoder. Their resolved
MIT-licensed dependency family includes **Vortice.DXGI 3.6.2**,
**Vortice.DirectX 3.6.2**, **Vortice.Mathematics 1.9.2**,
**SharpGen.Runtime 2.2.0-beta**, and **SharpGen.Runtime.COM 2.2.0-beta**. The
application does not bundle a video codec; encoding is performed by Windows.

- Package: <https://www.nuget.org/packages/Vortice.Direct3D11/3.6.2>
- Package: <https://www.nuget.org/packages/Vortice.MediaFoundation/3.6.2>
- Upstream project: <https://github.com/amerkoleci/Vortice.Windows>
- License: <https://github.com/amerkoleci/Vortice.Windows/blob/cd916a03f206165bec67982ed501e88820d4182b/LICENSE>
- SharpGen runtime: <https://github.com/SharpGenTools/SharpGenTools>
- SharpGen license: <https://github.com/SharpGenTools/SharpGenTools/blob/a22348d2e1ff76dfbdc51d68800ed31e991d8b32/LICENSE.txt>

## Installer tooling

The optional Windows setup executable is compiled with **Inno Setup 7**. Inno
Setup is a build-time tool and is not included in the portable ZIP. Anyone
redistributing or commercially building the installer is responsible for
following the current Inno Setup license terms.

- Project and license information: <https://jrsoftware.org/isinfo.php>
- Download and signature-verification information: <https://jrsoftware.org/isdl.php>

## Technical references

The following open-source projects were consulted as primary technical references for the independently implemented loopback-CDP architecture:

- **Backdrop for Codex**, Apache License 2.0: <https://github.com/TogawaSakiko-desuwa/backdrop-for-codex>
- **Codex Dynamic Skin**, MIT License: <https://github.com/CCDawn/Codex-Dynamic-Skin>
- **Codex Dream Skin**, MIT License: <https://github.com/Fei-Away/Codex-Dream-Skin>

No wallpapers or screenshots from those projects are included.

OpenAI, Codex, Steam, Valve, and Wallpaper Engine are trademarks of their respective owners. Their names are used only to describe compatibility.
