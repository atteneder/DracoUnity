# DracoUnity

Unity package that integrates the [Draco 3D data compression library](https://google.github.io/draco) within Unity.

![Screenshot of loaded bunny meshes](https://github.com/atteneder/DracoUnityDemo/raw/master/Images/bunnies.png "Lots of Stanford bunny meshes loaded via DracoUnity")

It is a fork of the [existing Unity integration](https://github.com/google/draco/tree/master/unity) with the following improvements:

- Can be integrated into Projects easily via Package Manager
- Is magnitudes faster due to
  - Bulk memory copies instead of per vertex/index data copy
  - Multi-threaded via C# Job system
- Supports single meshes with more than 65536 vertices (old split algorithm was broken)
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
"com.atteneder.draco": "https://github.com/atteneder/DracoUnity.git",
```

It should look something like this:

```json
{
  "dependencies": {
    "com.atteneder.draco": "https://github.com/atteneder/DracoUnity.git",
    "com.unity.package-manager-ui": "2.1.2",
    "com.unity.modules.unitywebrequest": "1.0.0"
    ...
  }
}
```

Next time you open your project in Unity, it will download the package automatically. There's more detail about how to add packages via GIT URLs in the [Unity documentation](https://docs.unity3d.com/Manual/upm-git.html).

## Using

There's a simple demo project that shows how you can use it:

<https://github.com/atteneder/DracoUnityDemo>


TODO: add usage example code

## Support

Like this demo? You can show your appreciation and ...

[![Buy me a coffee](https://az743702.vo.msecnd.net/cdn/kofi1.png?v=0)](https://ko-fi.com/C0C3BW7G)

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