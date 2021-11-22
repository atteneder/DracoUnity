# Draco 3D Data Compression Unity Package

[![openupm](https://img.shields.io/npm/v/com.atteneder.draco?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.atteneder.draco/)
[![GitHub issues](https://img.shields.io/github/issues/atteneder/DracoUnity)](https://github.com/atteneder/DracoUnity/issues)
[![GitHub license](https://img.shields.io/github/license/atteneder/DracoUnity)](https://github.com/atteneder/DracoUnity/blob/main/LICENSE.md)

Unity package that integrates the [Draco 3D data compression library](https://google.github.io/draco) within Unity.

![Screenshot of loaded bunny meshes](https://github.com/atteneder/DracoUnityDemo/raw/main/Images/bunnies.png "Lots of Stanford bunny meshes loaded via Draco 3D Data Compression Unity Package")

Following build targets are supported

- WebGL
- iOS (arm64 and armv7a)
- Android (x86, arm64 and armv7a)
- Windows (64 and 32 bit)
- Universal Windows Platform (x64,x86,ARM,ARM64)
- macOS (Apple Silicon an Intel)
- Linux (64 and 32 bit)
- Lumin / Magic Leap

> Note: Burst support is broken on iOS builds at the moment. Please deactivate Burst AOT in the project settings until this is resolved.

## Installing

The easiest way to install is to download and open the [Installer Package](https://package-installer.glitch.me/v1/installer/OpenUPM/com.atteneder.draco?registry=https%3A%2F%2Fpackage.openupm.com&scope=com.atteneder)

It runs a script that installs the Draco 3D Data Compression Unity Package via a [scoped registry](https://docs.unity3d.com/Manual/upm-scoped.html). After that it is listed in the *Package Manager* and can be updated from there.

<details><summary>Alternative: Install via GIT URL</summary>

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

Next time you open your project in Unity, it will download the package automatically. You have to have a GIT LFS client (large file support) installed on your system. Otherwise you will get an error that the native library file (dll on Windows) is corrupt. There's more detail about how to add packages via GIT URLs in the [Unity documentation](https://docs.unity3d.com/Manual/upm-git.html).

</details>

## Using

### Mesh result

Minimalistic way of loading a draco file ([source][DracoDemo]):

```csharp
public class DracoDemo : MonoBehaviour {
    
    public string filePath;

    async void Start() {
        
        // Load file into memory
        var fullPath = Path.Combine(Application.streamingAssetsPath, filePath);
        var data = File.ReadAllBytes(fullPath);
        
        // Convert data to Unity mesh
        var draco = new DracoMeshLoader();
        // Async decoding has to start on the main thread and spawns multiple C# jobs.
        var mesh = await draco.ConvertDracoMeshToUnity(data);
        
        if (mesh != null) {
            // Use the resulting mesh
            GetComponent<MeshFilter>().mesh= mesh;
        }
    }
}
```

### Using MeshDataArray

Starting with Unity 2020.2 you can create Meshes efficiently via [`MeshDataArray`][MeshDataArray].

The important difference is that instead of returning a `Mesh` directly, it just configures the `MeshData` properly and fills its buffers. It's up to the user to:

- Create the `Mesh` instance(s)
- Apply the data via `Mesh.ApplyAndDisposeWritableMeshData`
- In case the mesh had bone weight data, apply and dispose those as well (optional extra step)

Here's an examply how to do this ([source][DracoDemoMeshData]):

```csharp
public class DracoDemoMeshData : MonoBehaviour {
    
    public string filePath;

    public bool requireNormals;
    public bool requireTangents;
    
    async void Start() {
        
        // Load file into memory
        var fullPath = Path.Combine(Application.streamingAssetsPath, filePath);
        var data = File.ReadAllBytes(fullPath);
        
        // Convert data to Unity mesh
        var draco = new DracoMeshLoader();

        // Allocate single mesh data (you can/should bulk allocate multiple at once, if you're loading multiple draco meshes) 
        var meshDataArray = Mesh.AllocateWritableMeshData(1);
        
        // Async decoding has to start on the main thread and spawns multiple C# jobs.
        var result = await draco.ConvertDracoMeshToUnity(
            meshDataArray[0],
            data,
            requireNormals, // Set to true if you require normals. If Draco data does not contain them, they are allocated and we have to calculate them below
            requireTangents // Retrieve tangents is not supported, but this will ensure they are allocated and can be calculated later (see below)
            );
        
        if (result.success) {
            
            // Apply onto new Mesh
            var mesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,mesh);
            
            // If Draco mesh has bone weigths, apply them now.
            // To get these, you have to supply the correct attribute IDs
            // to `ConvertDracoMeshToUnity` above (optional paramters).
            if (result.boneWeightData != null) {
                result.boneWeightData.ApplyOnMesh(mesh);
                result.boneWeightData.Dispose();
            }
            
            if (result.calculateNormals) {
                // If draco didn't contain normals, calculate them.
                mesh.RecalculateNormals();
            }
            if (requireTangents) {
                // If required (e.g. for consistent specular shading), calculate tangents
                mesh.RecalculateTangents();
            }
            
            // Use the resulting mesh
            GetComponent<MeshFilter>().mesh = mesh;
        }
    }
}
```

### Details and where to go next

See the signatures of all [`DracoMeshLoader.ConvertDracoMeshToUnity`][DracoMeshLoader] variants to see all options available.

The examples above and more can be found in the [DracoUnityDemo][DracoUnityDemo] project.

### Troubleshooting - Missing code signing

The binary libraries used in this package are not code-signed. macOS in particular will not let you load the `ktx_unity.bundle` for that reason (see [issue](https://github.com/atteneder/DracoUnity/issues/4)).

Here's the steps to make it work on macOS

1. When you first open a project with DracoUnity (or add the package), you get prompted to remove the "broken" ktx_unity.bundle. Don't do it and click "cancel" instead.
2. Open the macOS "System Preferencess" and go to "Security & Privacy". At the bottom of the "General" tab you should see a warning about ktx_unity.bundle. Click the "Allow anyways" button besides it.
3. Restart Unity
4. Now you get another, similar prompt (see step 1) with the third option "Open". Click it
5. Now it should work (at least for development on your machine)

If you want to deploy your software using DracoUnity you either have to

- Wait until there is proper code-sign setup (watch this project or subscribe to the [corresponding issue](https://github.com/atteneder/DracoUnity/issues/4)).
- Build your own library from [the source draco repository](https://github.com/atteneder/draco) (note that it's not the original Google repository) and sign it with your own credentials/Apple profile.

## Origin

This project is a fork of the [existing Unity integration](https://github.com/google/draco/tree/master/unity)

### Differences

- Encoding support
- Can be integrated into Projects easily via Package Manager
- Is magnitudes faster due to
  - Bulk memory copies instead of per vertex/index data copy
  - Multi-threaded via C# Job system
  - Burst compiler
  - Using Unity's Advanced Mesh API
- Supports coordinate space conversion (from right-handed Y-up to Unity's left-handed Y-up by flipping the Z-axis; optional; required in glTF context)
- Supports single meshes with more than 65536 vertices (old split algorithm was broken)
- Supports loading joint index and joint weights for skinning
- Corrects tangents by re-calculating them if necessary
- Point cloud support
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

[DracoDemo]: https://github.com/atteneder/DracoUnityDemo/blob/main/Assets/Scripts/DracoDemo.cs
[DracoDemoMeshData]: https://github.com/atteneder/DracoUnityDemo/blob/main/Assets/Scripts/DracoDemoMeshData.cs
[DracoMeshLoader]: https://github.com/atteneder/DracoUnity/blob/main/Runtime/Scripts/DracoMeshLoader.cs
[DracoUnityDemo]: https://github.com/atteneder/DracoUnityDemo

[MeshDataArray]: https://docs.unity3d.com/2021.2/Documentation/ScriptReference/Mesh.MeshDataArray.html
