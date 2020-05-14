# DracoUnity

Unity package that integrates the [Draco 3D data compression library](https://google.github.io/draco) within Unity.

![Screenshot of loaded bunny meshes](https://github.com/atteneder/DracoUnityDemo/raw/master/Images/bunnies.png "Lots of Stanford bunny meshes loaded via DracoUnity")

It is a fork of the [existing Unity integration](https://github.com/google/draco/tree/master/unity)

## Differences

DracoUnity assumes Draco meshes to be right-handed Y-up coordinates and converts them to Unity's left-handed Y-up by flipping the Z-axis.

## Improvements

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

## Installing

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

## Using

There's a simple demo project that shows how you can use it:

<https://github.com/atteneder/DracoUnityDemo>


TODO: add usage example code

## Support

Like this demo? You can show your appreciation and ...

[![Buy me a coffee](https://az743702.vo.msecnd.net/cdn/kofi1.png?v=0)](https://ko-fi.com/C0C3BW7G)

## Develop

To develop this package, check out the repository and add it as local repository in the Unity Package Manager.

### Build Draco library

In case you need a custom or updated build of the dracodec_unity library, first read the [original documentation](https://github.com/google/draco/tree/master/unity#build-from-source) on how to build it.

Additionally make sure, you enable the `BUILD_FOR_GLTF` flag, in order to enable all necessary features.

#### CMake config

General cmake command to build for the current platform

```bash
cmake ../draco \
-DCMAKE_BUILD_TYPE=Release \
-DBUILD_FOR_GLTF=TRUE \
-DBUILD_UNITY_PLUGIN=TRUE
```

#### CMake config iOS

iOS needs some additional params

```bash
cmake ../draco -G Xcode \
-DCMAKE_SYSTEM_NAME=iOS \
-DCMAKE_OSX_ARCHITECTURES=armv7\;armv7s\;arm64 \
-DCMAKE_OSX_DEPLOYMENT_TARGET=10.0 \
-DCMAKE_XCODE_ATTRIBUTE_ONLY_ACTIVE_ARCH=NO \
-DBUILD_FOR_GLTF=TRUE \
-DBUILD_UNITY_PLUGIN=TRUE
```

#### WebGL emscripten

Emscripten can compile code into a bitcode library (.bc), which Unity links during its Build.

This bitcode library was built with a custom command like this:

```bash
emcc -O2 -std=c++11 -I. -Iinc -o dracodec_unity.bc -s WASM=1 \
-DDRACO_MESH_COMPRESSION_SUPPORTED -DDRACO_NORMAL_ENCODING_SUPPORTED -DDRACO_STANDARD_EDGEBREAKER_SUPPORTED \
<list of all needed draco source files>
```

Make sure to use the fastcomp variant of emscripten. The LLVM did not work for me (with Unity 2019.2)

TODO: Properly build library via CMake.

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
