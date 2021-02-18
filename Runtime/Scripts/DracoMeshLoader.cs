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
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Draco {

    public class DracoMeshLoader {
        
        /// <summary>
        /// If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).
        /// </summary>
        bool convertSpace;

        /// <summary>
        /// Create a DracoMeshLoader instance which let's you decode Draco data.
        /// </summary>
        /// <param name="convertSpace">If true, coordinate space is converted from right-hand (like in glTF) to left-hand (Unity).</param>
        public DracoMeshLoader(bool convertSpace = true) {
            this.convertSpace = convertSpace;
        }

        public struct DecodeResult {
            /// <summary>
            /// True if the decoding was successful
            /// </summary>
            public bool success;
            
            /// <summary>
            /// True, if the normals were marked required, but not present in Draco mesh.
            /// They have to get calculated.
            /// </summary>
            public bool calculateNormals;
        }

        public const MeshUpdateFlags defaultMeshUpdateFlags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;
        
        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(
            NativeSlice<byte> encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
            )
        {
            var encodedDataPtr = GetUnsafeReadOnlyIntPtr(encodedData);
#if UNITY_2020_2_OR_NEWER
            var meshDataArray = Mesh.AllocateWritableMeshData(1); 
            var mesh = meshDataArray[0];
            var result = await ConvertDracoMeshToUnity(mesh, encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
            if (!result.success) {
                meshDataArray.Dispose();
                return null;
            }
            var unityMesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,unityMesh,defaultMeshUpdateFlags);
            if (result.calculateNormals) {
                unityMesh.RecalculateNormals();
            }
            if (requireTangents) {
                unityMesh.RecalculateTangents();
            }
            return unityMesh;
#else
            return await ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
#endif
        }

        /// <summary>
        /// Decodes a Draco mesh
        /// </summary>
        /// <param name="encodedData">Compressed Draco data</param>
        /// <returns>Unity Mesh or null in case of errors</returns>
        public async Task<Mesh> ConvertDracoMeshToUnity(
            byte[] encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
            )
        {
            var encodedDataPtr = PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
#if UNITY_2020_2_OR_NEWER
            using (var meshDataArray = Mesh.AllocateWritableMeshData(1)) {
                var mesh = meshDataArray[0];
                var result = await ConvertDracoMeshToUnity(mesh, encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
                UnsafeUtility.ReleaseGCObject(gcHandle);
                if (!result.success) return null;
                var unityMesh = new Mesh();
                Mesh.ApplyAndDisposeWritableMeshData(meshDataArray,unityMesh,defaultMeshUpdateFlags);
                if (result.calculateNormals) {
                    unityMesh.RecalculateNormals();
                }
                if (requireTangents) {
                    unityMesh.RecalculateTangents();
                }
                return unityMesh;
            }
#else
            var result = await ConvertDracoMeshToUnity(encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
            UnsafeUtility.ReleaseGCObject(gcHandle);
            return result;
#endif
        }

#if UNITY_2020_2_OR_NEWER
        public async Task<DecodeResult> ConvertDracoMeshToUnity(
            Mesh.MeshData mesh,
            byte[] encodedData,
            bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
            )
        {
            var encodedDataPtr = PinGCArrayAndGetDataAddress(encodedData, out var gcHandle);
            var result = await ConvertDracoMeshToUnity(mesh, encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
            UnsafeUtility.ReleaseGCObject(gcHandle);
            return result;
        }
        
        public async Task<DecodeResult> ConvertDracoMeshToUnity(Mesh.MeshData mesh, NativeArray<byte> encodedData, bool requireNormals = false,
            bool requireTangents = false,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
            )
        {
            var encodedDataPtr = GetUnsafeReadOnlyIntPtr(encodedData);
            return await ConvertDracoMeshToUnity(mesh, encodedDataPtr, encodedData.Length, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
        }
#endif
        
#if UNITY_2020_2_OR_NEWER
        async Task<DecodeResult> ConvertDracoMeshToUnity(
            Mesh.MeshData mesh,
            IntPtr encodedData,
            int size,
            bool requireNormals,
            bool requireTangents,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
        )
#else
        async Task<Mesh> ConvertDracoMeshToUnity(
            IntPtr encodedData,
            int size,
            bool requireNormals,
            bool requireTangents,
            int weightsAttributeId = -1,
            int jointsAttributeId = -1
            )
#endif
        {
#if UNITY_2020_2_OR_NEWER
            var dracoNative = new DracoNative(mesh,convertSpace);
            var result = new DecodeResult();
#else
            var dracoNative = new DracoNative(convertSpace);
#endif
            await WaitForJobHandle(dracoNative.Init(encodedData, size));
            if (dracoNative.ErrorOccured()) {
#if UNITY_2020_2_OR_NEWER
                return result;
#else
                return null;
#endif
            }
            if (!requireNormals && requireTangents) {
                // Sanity check: We need normals to calculate tangents
                requireNormals = true;
            }
#if UNITY_2020_2_OR_NEWER
            dracoNative.CreateMesh(out result.calculateNormals, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
#else
            dracoNative.CreateMesh(out var calculateNormals, requireNormals, requireTangents, weightsAttributeId, jointsAttributeId);
#endif      
            await WaitForJobHandle(dracoNative.DecodeVertexData());
            var error = dracoNative.ErrorOccured();
            dracoNative.DisposeDracoMesh();
            if (error) {
#if UNITY_2020_2_OR_NEWER
                return result;
#else
                return null;
#endif
            }

#if !UNITY_2020_2_OR_NEWER
            var result = dracoNative.PopulateMeshData();
            if (calculateNormals) {
                // TODO: Consider doing this in a threaded Job
                Profiler.BeginSample("RecalculateNormals");
                result.RecalculateNormals();
                Profiler.EndSample();
            }
            if (requireTangents) {
                // TODO: Consider doing this in a threaded Job
                Profiler.BeginSample("RecalculateTangents");
                result.RecalculateTangents();
                Profiler.EndSample();
            }
#else
            result.success = dracoNative.PopulateMeshData();
#endif
            return result;
        }

        static async Task WaitForJobHandle(JobHandle jobHandle) {
            while (!jobHandle.IsCompleted) {
                await Task.Yield();
            }
            jobHandle.Complete();
        }
        
        static unsafe IntPtr GetUnsafeReadOnlyIntPtr(NativeSlice<byte> encodedData) {
            return (IntPtr) encodedData.GetUnsafeReadOnlyPtr();
        }
        
        static unsafe IntPtr PinGCArrayAndGetDataAddress(byte[] encodedData, out ulong gcHandle) {
            return (IntPtr) UnsafeUtility.PinGCArrayAndGetDataAddress(encodedData, out gcHandle);
        }
    }
}
