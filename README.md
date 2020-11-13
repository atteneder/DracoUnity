# Draco 3D Data Compression Unity Package

[![openupm](https://img.shields.io/npm/v/com.atteneder.draco?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.atteneder.draco/)

Unity package that integrates the [Draco 3D data compression library](https://google.github.io/draco) within Unity.

![Screenshot of loaded bunny meshes](https://github.com/atteneder/DracoUnityDemo/raw/master/Images/bunnies.png "Lots of Stanford bunny meshes loaded via Draco 3D Data Compression Unity Package")

Following build targets are supported

- WebGL
- iOS (arm64 and armv7a)
- Android (x86, arm64 and armv7a)
- Windows (64 and 32 bit)
- Universal Windows Platform (x64,x86,ARM,ARM64)
- macOS (Apple Silicon an Intel)
- Linux (64 and 32 bit)

## Installing

The easiest way to install is to download and open the [Installer Package](https://package-installer.glitch.me/v1/installer/OpenUPM/com.atteneder.draco?registry=https%3A%2F%2Fpackage.openupm.com&scope=com.atteneder)

It runs a script that installs the Draco 3D Data Compression Unity Package via a [scoped registry](https://docs.unity3d.com/Manual/upm-scoped.html). After that it is listed in the *Package Manager* and can be updated from there.

<details><summary>Alternative: Install via GIT URL</summary>

You have to manually add the package's URL into your [project manifest](https://docs.unity3d.com/Manual/upm-manifestPrj.html)

Inside your Unity project there's the folder `Packages` containing a file called `manifest.json`. You have to open it and add the following line inside the `dependencies` category:

```json
"com.atteneder.draco": "https://gitlab.com/atteneder/DracoUnity.git",
```

It should look something like this:

```json
{
  "dependencies": {
    "com.atteneder.draco": "https://gitlab.com/atteneder/DracoUnity.git",
    "com.unity.package-manager-ui": "2.1.2",
    "com.unity.modules.unitywebrequest": "1.0.0"
    ...
  }
}
```

Next time you open your project in Unity, it will download the package automatically. You have to have a GIT LFS client (large file support) installed on your system. Otherwise you will get an error that the native library file (dll on Windows) is corrupt. There's more detail about how to add packages via GIT URLs in the [Unity documentation](https://docs.unity3d.com/Manual/upm-git.html).

</details>

## Using

There's a simple demo project that shows how you can use it:

<https://github.com/atteneder/DracoUnityDemo>


TODO: add usage example code

## Origin

This project is a fork of the [existing Unity integration](https://github.com/google/draco/tree/master/unity)

### Differences

Draco 3D Data Compression Unity Package assumes Draco meshes to be right-handed Y-up coordinates and converts them to Unity's left-handed Y-up by flipping the Z-axis.

### Improvements

- Can be integrated into Projects easily via Package Manager
- Is magnitudes faster due to
  - Bulk memory copies instead of per vertex/index data copy
  - Multi-threaded via C# Job system
- Supports single meshes with more than 65536 vertices (old split algorithm was broken)
- Supports loading joint index and joint weights for skinning
- Corrects tangents by re-calculating them if necessary
- Additional native libs and support for platforms
  - WebGL
  - iOS armv7(s) and arm64
  - Windows 32-bit
  - Linux 64-bit and 32-bit
  - Android x86
  - Universal Windows Platform (x64,x86,ARM,ARM64)
  - macOS Apple Silicon

## Support

Like this demo? You can show your appreciation and ...

[![Buy me a coffee](https://az743702.vo.msecnd.net/cdn/kofi1.png?v=0)](https://ko-fi.com/C0C3BW7G)

## Develop

To develop this package, check out the repository and add it as local repository in the Unity Package Manager.

### Build Draco library

The native libraries are built via CI in this [GitHub action](https://github.com/atteneder/draco/actions?query=workflow%3A%22Draco+Decoder+Unity+library+CI%22)

Look into the YAML file to see how the project is built with CMake.

## License

Copyright (c) 2019 Andreas Atteneder, All Rights Reserved.
Licensed under the Apache License, Version 2.0 (the "License");
you may not use files in this repository except in compliance with the License.
You may obtain a copy of the License at

   <http://www.apache.org/licenses/LICENSE-2.0>

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.

## Third party notice

Builds upon and includes builds of [Google](https://about.google)'s [Draco 3D data compression library](https://google.github.io/draco) (released under the terms of Apache License 2.0).
