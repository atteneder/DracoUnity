# Draco 3D Data Compression Unity Package

Unity package that integrates the [Draco 3D data compression library](https://google.github.io/draco) within Unity.

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


[DracoDemo]: https://github.com/atteneder/DracoUnityDemo/blob/main/Assets/Scripts/DracoDemo.cs
[DracoDemoMeshData]: https://github.com/atteneder/DracoUnityDemo/blob/main/Assets/Scripts/DracoDemoMeshData.cs
[DracoMeshLoader]: https://github.com/atteneder/DracoUnity/blob/main/Runtime/Scripts/DracoMeshLoader.cs
[DracoUnityDemo]: https://github.com/atteneder/DracoUnityDemo

[MeshDataArray]: https://docs.unity3d.com/2021.2/Documentation/ScriptReference/Mesh.MeshDataArray.html
