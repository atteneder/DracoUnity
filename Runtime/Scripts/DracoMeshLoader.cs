// Copyright 2017 The Draco Authors.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public unsafe class DracoMeshLoader
{
  // These values must be exactly the same as the values in draco_types.h.
  // Attribute data type.
  enum DataType {
    DT_INVALID = 0,
    DT_INT8,
    DT_UINT8,
    DT_INT16,
    DT_UINT16,
    DT_INT32,
    DT_UINT32,
    DT_INT64,
    DT_UINT64,
    DT_FLOAT32,
    DT_FLOAT64,
    DT_BOOL
  };

  // These values must be exactly the same as the values in
  // geometry_attribute.h.
  // Attribute type.
  enum AttributeType {
    INVALID = -1,
    POSITION = 0,
    NORMAL,
    COLOR,
    TEX_COORD,
    // A special id used to mark attributes that are not assigned to any known
    // predefined use case. Such attributes are often used for a shader specific
    // data.
    GENERIC
  };

  // The order must be consistent with C++ interface.
  [StructLayout (LayoutKind.Sequential)] public struct DracoData
  {
    public int dataType;
    public IntPtr data;
  }

  [StructLayout (LayoutKind.Sequential)] public struct DracoAttribute
  {
    public int attributeType;
    public int dataType;
    public int numComponents;
    public int uniqueId;
  }

  [StructLayout (LayoutKind.Sequential)] public struct DracoMesh
  {
    public int numFaces;
    public int numVertices;
    public int numAttributes;
  }

  // Release data associated with DracoMesh.
  [DllImport ("dracodec_unity")] private static extern void ReleaseDracoMesh(
      DracoMesh**mesh);
  // Release data associated with DracoAttribute.
  [DllImport ("dracodec_unity")] private static extern void
      ReleaseDracoAttribute(DracoAttribute**attr);
  // Release attribute data.
  [DllImport ("dracodec_unity")] private static extern void ReleaseDracoData(
      DracoData**data);

  // Decodes compressed Draco::Mesh in buffer to mesh. On input, mesh
  // must be null. The returned mesh must released with ReleaseDracoMesh.
  [DllImport ("dracodec_unity")] private static extern int DecodeDracoMesh(
      byte* buffer, int length, DracoMesh**mesh);

  // Returns the DracoAttribute at index in mesh. On input, attribute must be
  // null. The returned attr must be released with ReleaseDracoAttribute.
  [DllImport ("dracodec_unity")] private static extern bool GetAttribute(
      DracoMesh* mesh, int index, DracoAttribute**attr);
  // Returns the DracoAttribute of type at index in mesh. On input, attribute
  // must be null. E.g. If the mesh has two texture coordinates then
  // GetAttributeByType(mesh, AttributeType.TEX_COORD, 1, &attr); will return
  // the second TEX_COORD attribute. The returned attr must be released with
  // ReleaseDracoAttribute.
  [DllImport ("dracodec_unity")] private static extern bool GetAttributeByType(
      DracoMesh* mesh, AttributeType type, int index, DracoAttribute**attr);
  // Returns the DracoAttribute with unique_id in mesh. On input, attribute
  // must be null.The returned attr must be released with
  // ReleaseDracoAttribute.
  [DllImport ("dracodec_unity")] private static extern bool
      GetAttributeByUniqueId(DracoMesh* mesh, int unique_id,
                             DracoAttribute**attr);

  // Returns an array of indices as well as the type of data in data_type. On
  // input, indices must be null. The returned indices must be released with
  // ReleaseDracoData.
  [DllImport ("dracodec_unity")] private static extern bool GetMeshIndices(
      DracoMesh* mesh, DracoData**indices);
  // Returns an array of attribute data as well as the type of data in
  // data_type. On input, data must be null. The returned data must be
  // released with ReleaseDracoData.
  [DllImport ("dracodec_unity")] private static extern bool GetAttributeData(
      DracoMesh* mesh, DracoAttribute* attr, DracoData**data);

  public int LoadMeshFromAsset(string assetName, ref List<Mesh> meshes)
  {
    TextAsset asset =
        Resources.Load(assetName, typeof(TextAsset)) as TextAsset;
    if (asset == null) {
      Debug.LogError ("Didn't load file!");
      return -1;
    }
    byte[] encodedData = asset.bytes;
    // Debug.Log(encodedData.Length.ToString());
    if (encodedData.Length == 0) {
      Debug.LogError ("Didn't load encoded data!");
      return -1;
    }
    return ConvertDracoMeshToUnity(encodedData, ref meshes);
  }

  // Decodes a Draco mesh, creates a Unity mesh from the decoded data and
  // adds the Unity mesh to meshes. encodedData is the compressed Draco mesh.
  public int ConvertDracoMeshToUnity(NativeArray<byte> encodedData,
    ref List<Mesh> meshes) {
    var encodedDataPtr = (byte*) encodedData.GetUnsafeReadOnlyPtr();
    return ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length, ref meshes);
  }
  
  // Decodes a Draco mesh, creates a Unity mesh from the decoded data and
  // adds the Unity mesh to meshes. encodedData is the compressed Draco mesh.
  public int ConvertDracoMeshToUnity(byte[] encodedData,
    ref List<Mesh> meshes) {
    var encodedDataPtr = (byte*) UnsafeUtility.PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
    var result = ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length, ref meshes);
    UnsafeUtility.ReleaseGCObject(gcHandle);
    return result;
  }
  
  // Decodes a Draco mesh, creates a Unity mesh from the decoded data and
  // adds the Unity mesh to meshes. encodedData is the compressed Draco mesh.
  int ConvertDracoMeshToUnity(byte* encodedData, int size,
    ref List<Mesh> meshes)
  {
    Profiler.BeginSample("DecodeDracoMesh");
    DracoMesh *mesh = null;
    var decodeDracoMesh = DecodeDracoMesh(encodedData, size, &mesh);
    Profiler.EndSample();
    if (decodeDracoMesh <= 0) {
      Debug.LogError("Failed: Decoding error.");
      return -1;
    }
    
    Mesh unityMesh = CreateUnityMesh(mesh);
    meshes.Add(unityMesh);

    int numFaces = mesh->numFaces;
    Profiler.BeginSample("ReleaseDracoMesh");
    ReleaseDracoMesh(&mesh);
    Profiler.EndSample();
    return numFaces;
  }
  
  class AttributeMap {
    public DracoAttribute* dracoAttribute;
    public VertexAttributeFormat format;
    public int offset;
    public int stream;
    
    public AttributeMap (DracoAttribute* dracoAttribute, VertexAttributeFormat format) {
      this.dracoAttribute = dracoAttribute;
      this.format = format;
      offset = 0;
      stream = 0;
    }

    public int numComponents => dracoAttribute->numComponents;
    public int elementSize => DataTypeSize((DataType)dracoAttribute->dataType) * dracoAttribute->numComponents;

    public void Dispose() {
      var tmp = dracoAttribute;
      ReleaseDracoAttribute(&tmp);
      dracoAttribute = null;
    }
  }

  struct GetDracoDataJob : IJob {
    [ReadOnly]
    [NativeDisableUnsafePtrRestriction]
    public DracoMesh* dracoMesh;

    [ReadOnly]
    [NativeDisableUnsafePtrRestriction]
    public DracoAttribute* attribute;
    
    [WriteOnly]
    [NativeDisableUnsafePtrRestriction]
    public byte* dstPtr;
    
    public void Execute() {
      Profiler.BeginSample($"CreateUnityMesh.CopyVertexData{(AttributeType)attribute->attributeType}");
      DracoData* data = null;
      GetAttributeData(dracoMesh, attribute, &data);
      var elementSize = DataTypeSize((DataType)data->dataType) * attribute->numComponents;
      UnsafeUtility.MemCpy(dstPtr, (void*)data->data, elementSize*dracoMesh->numVertices);
      Profiler.EndSample();
      Profiler.BeginSample("CreateUnityMesh.ReleaseData");
      ReleaseDracoData(&data);
      Profiler.EndSample();
    }
  }

  struct GetDracoDataInterleavedJob : IJob {

    [ReadOnly]
    [NativeDisableUnsafePtrRestriction]
    public DracoMesh* dracoMesh;

    [ReadOnly]
    [NativeDisableUnsafePtrRestriction]
    public DracoAttribute* attribute;
    
    [ReadOnly]
    public int stride;
    
    [WriteOnly]
    [NativeDisableUnsafePtrRestriction]
    public byte* dstPtr;
    
    public void Execute() {
      Profiler.BeginSample($"CreateUnityMesh.CopyVertexData{(AttributeType)attribute->attributeType}");
      DracoData* data = null;
      GetAttributeData(dracoMesh, attribute, &data);
      var elementSize = DataTypeSize((DataType)data->dataType) * attribute->numComponents;
      for (var v = 0; v < dracoMesh->numVertices; v++) {
        UnsafeUtility.MemCpy(dstPtr+(stride*v), ((byte*)data->data)+(elementSize*v), elementSize);
      }
      Profiler.EndSample();
      Profiler.BeginSample("CreateUnityMesh.ReleaseData");
      ReleaseDracoData(&data);
      Profiler.EndSample();
    }
  }
  
  // Creates a Unity mesh from the decoded Draco mesh.
  public Mesh CreateUnityMesh(DracoMesh *dracoMesh)
  {
    Profiler.BeginSample("CreateUnityMesh");
    
    Profiler.BeginSample("CreateUnityMesh.Allocate");
    
    var attributes = new Dictionary<VertexAttribute,AttributeMap>(dracoMesh->numAttributes);
    
    void CreateAttributeMaps(AttributeType attributeType, int count) {
      for (var i = 0; i < count; i++) {
        var type = GetVertexAttribute(attributeType,i);
        if (!type.HasValue) {
#if UNITY_EDITOR
          // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
          Debug.LogWarning($"Unknown attribute {attributeType}!");
#endif
          continue;
        }
        if (attributes.ContainsKey(type.Value)) {
#if UNITY_EDITOR
          // ReSharper disable once Unity.PerformanceCriticalCodeInvocation
          Debug.LogWarning($"Multiple {type.Value} attributes!");
#endif
          continue;
        }
        DracoAttribute* attribute;
        if (GetAttributeByType(dracoMesh, attributeType, i, &attribute)) {
          var format = GetVertexAttributeFormat((DataType)attribute->dataType);
          if (!format.HasValue) {
            continue;
          }
          var map = new AttributeMap(attribute,format.Value);
          attributes[type.Value] = map;
        }
        else {
          // attributeType was not found
          break;
        }
      }
    }

    CreateAttributeMaps(AttributeType.POSITION,1);
    CreateAttributeMaps(AttributeType.NORMAL,1);
    CreateAttributeMaps(AttributeType.COLOR,1);
    CreateAttributeMaps(AttributeType.TEX_COORD,8);
    
    // TODO: If konw, query generic attributes by ID
    // CreateAttributeMaps(AttributeType.GENERIC,2);

    const int maxStreamCount = 4;

    var streamStrides = new int[maxStreamCount];
    
    var streamMemberCount = new int[maxStreamCount];
    
    int streamIndex = 0;
    foreach (var pair in attributes) {
      // Naive stream assignment:
      // First 3 attributes get a dedicated stream (#1,#2 and #3 respectivly)
      // 4th and following get assigned to stream #4
      // TODO: Make smarter stream assignment decision
      var attributeMap = pair.Value;
      var elementSize = attributeMap.elementSize;
      attributeMap.offset = streamStrides[streamIndex];
      attributeMap.stream = streamIndex;
      streamStrides[streamIndex] += elementSize;
      streamMemberCount[streamIndex]++;
      if (streamIndex < maxStreamCount) {
        streamIndex++;
      }
    }
    int streamCount = streamIndex;
    
    var newIndices = new NativeArray<uint>(dracoMesh->numFaces * 3, Allocator.Temp);

    var vData = new NativeArray<byte>[streamCount];
    var vDataPtr = new byte*[streamCount];
    for (streamIndex = 0; streamIndex < streamCount; streamIndex++) {
      vData[streamIndex] = new NativeArray<byte>(streamStrides[streamIndex]*dracoMesh->numVertices, Allocator.Temp);
      vDataPtr[streamIndex] = (byte*) vData[streamIndex].GetUnsafePtr();
    }
    Profiler.EndSample(); // CreateUnityMesh.Allocate
    
    Profiler.BeginSample("CreateUnityMesh.CopyIndices");
    // Copy face indices.
    // TODO: Jobify
    DracoData *indicesData;
    GetMeshIndices(dracoMesh, &indicesData);
    int indexSize = DataTypeSize((DataType)indicesData->dataType);
    int *indices = (int*) indicesData->data;
    UnsafeUtility.MemCpy(newIndices.GetUnsafePtr(), indices,
                         newIndices.Length * indexSize);
    Profiler.EndSample(); // CreateUnityMesh.CopyIndices
    Profiler.BeginSample("CreateUnityMesh.ReleaseIndices");
    ReleaseDracoData(&indicesData);
    Profiler.EndSample(); // CreateUnityMesh.ReleaseIndices

    foreach (var pair in attributes) {
      var map = pair.Value;
      if (streamMemberCount[map.stream] > 1) {
        var job = new GetDracoDataInterleavedJob() {
          dracoMesh=dracoMesh,
          attribute=map.dracoAttribute,
          stride=streamStrides[map.stream],
          dstPtr=vDataPtr[map.stream]+map.offset
        };
        // TODO: Jobify!
        // job.Schedule();
        job.Run();
      }
      else {
        var job = new GetDracoDataJob() {
          dracoMesh=dracoMesh,
          attribute=map.dracoAttribute,
          dstPtr=vDataPtr[map.stream]+map.offset
        };
        // TODO: Jobify!
        // job.Schedule();
        job.Run();
      }
    }

    Profiler.BeginSample("CreateUnityMesh.CreateMesh");
    var mesh = new Mesh();
    
    mesh.SetIndexBufferParams(newIndices.Length,IndexFormat.UInt32);
    
    var vertexParams = new List<VertexAttributeDescriptor>(dracoMesh->numAttributes);
    foreach (var pair in attributes) {
      var map = pair.Value;
      vertexParams.Add(new VertexAttributeDescriptor(pair.Key, map.format, map.numComponents,map.stream));
      map.Dispose();
    }

    const MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
    
    // TODO: perform normal/tangent calculations if required
    // mesh.RecalculateNormals();
    // mesh.RecalculateTangents();
    
    mesh.SetVertexBufferParams(dracoMesh->numVertices,vertexParams.ToArray());

    for (streamIndex = 0; streamIndex <  streamCount; streamIndex++) {
      mesh.SetVertexBufferData(vData[streamIndex],0,0,vData[streamIndex].Length,streamIndex,flags);
    }
    
    mesh.SetIndexBufferData(newIndices,0,0,newIndices.Length);
    mesh.subMeshCount = 1;
    
    mesh.SetSubMesh(0,new SubMeshDescriptor(0,newIndices.Length), flags );

    Profiler.EndSample(); // CreateUnityMesh.CreateMesh
    Profiler.EndSample(); // CreateUnityMesh
    return mesh;
  }

  static int DataTypeSize(DataType dt) {
    switch (dt) {
      case DataType.DT_INT8:
      case DataType.DT_UINT8:
        return 1;
      case DataType.DT_INT16:
      case DataType.DT_UINT16:
        return 2;
      case DataType.DT_INT32:
      case DataType.DT_UINT32:
        return 4;
      case DataType.DT_INT64:
      case DataType.DT_UINT64:
        return 8;
      case DataType.DT_FLOAT32:
        return 4;
      case DataType.DT_FLOAT64:
        return 8;
      case DataType.DT_BOOL:
        return 1;
      default:
        return -1;
    }

  }
  
  VertexAttributeFormat? GetVertexAttributeFormat(DataType inputType) {
    switch (inputType) {
      case DataType.DT_INT8:
        return VertexAttributeFormat.SInt8;
      case DataType.DT_UINT8:
        return VertexAttributeFormat.UInt8;
      case DataType.DT_INT16:
        return VertexAttributeFormat.SInt16;
      case DataType.DT_UINT16:
        return VertexAttributeFormat.UInt16;
      case DataType.DT_INT32:
        return VertexAttributeFormat.SInt32;
      case DataType.DT_UINT32:
        return VertexAttributeFormat.UInt32;
      case DataType.DT_FLOAT32:
        return VertexAttributeFormat.Float32;
      // Not supported by Unity
      // TODO: convert to supported types
      // case DataType.DT_INT64:
      // case DataType.DT_UINT64:
      // case DataType.DT_FLOAT64:
      // case DataType.DT_BOOL:
      default:
        return null;
    }
  }

  VertexAttribute? GetVertexAttribute(AttributeType inputType, int index=0) {
    switch (inputType) {
      case AttributeType.POSITION:
        return VertexAttribute.Position;
      case AttributeType.NORMAL:
        return VertexAttribute.Normal;
      case AttributeType.COLOR:
        return VertexAttribute.Color;
      case AttributeType.TEX_COORD:
        return (VertexAttribute) ((int)VertexAttribute.TexCoord0+index);
      // case AttributeType.GENERIC:
      //   // TODO: map generic to possible candidates (BlendWeights, BlendIndices)
      //   break;
      default:
        return null;
    }
  }
}
